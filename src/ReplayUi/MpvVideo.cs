using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

// Нативный движок принадлежит видеопроцессу; UI не загружает DLL декодера.
public sealed class MpvVideo(nint surface, string cacheRoot, bool software = false, bool headless = false, bool nativeAudio = false) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _commands = [];
    private readonly Dictionary<ulong, TaskCompletionSource<bool>> _flagQueries = [];
    private MpvApi? _api;
    private nint _handle;
    private Task? _events;
    private TaskCompletionSource? _restart;
    private TaskCompletionSource _coreStateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _expectedEvent;
    private bool _restartArmed;
    private bool _disposed;
    private bool _ended;
    private double _position;
    private long _latestSeek;
    private long? _voDropped;
    private long? _decoderDropped;
    private ulong _nextCommand;
    private string? _fault;
    private string _evidence = "mpv: readiness not observed";

    public string ReadyEvidence { get { lock (_sync) return _evidence; } }
    public string? Fault { get { lock (_sync) return _fault; } }
    public bool HasEnded { get { lock (_sync) return _ended; } }
    public double PositionSeconds { get { lock (_sync) return _position; } }
    public MpvCounters Counters
    {
        get
        {
            lock (_sync)
            {
                // estimated-frame-number не является счётчиком декодирования или показа.
                // Оба источника пропусков независимы; неполную сумму не выдаём за общий итог.
                long? dropped = _voDropped is long vo && _decoderDropped is long decoder ? vo + decoder : null;
                return new MpvCounters(null, null, dropped);
            }
        }
    }

    public async Task OpenAsync(string file, double startSeconds, CancellationToken cancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        await _operation.WaitAsync(linked.Token);
        try
        {
            ThrowIfUnavailable();
            if (_handle == 0)
            {
                await Task.Run(() => Initialize(surface), linked.Token);
            }
            await CommandAsync(["set", "pause", "yes"], linked.Token);
            lock (_sync)
            {
                _ended = false;
                _position = 0;
                _voDropped = null;
                _decoderDropped = null;
            }
            Task ready = BeginRestartWait(8);
            try
            {
                string start = Math.Max(0, startSeconds).ToString("R", CultureInfo.InvariantCulture);
                await CommandAsync(["loadfile", file, "replace", "-1", $"start={start}"], linked.Token);
                await ready.WaitAsync(linked.Token);
                SetEvidence("MPV_EVENT_FILE_LOADED → MPV_EVENT_PLAYBACK_RESTART while paused; API restart complete, presentation checked externally");
            }
            finally { ClearRestartWait(); }
        }
        catch (Exception error) { RecordOperationFailure(error); throw; }
        finally { _operation.Release(); }
    }

    public async Task SeekAsync(double seconds, CancellationToken cancellation)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        cancellation.ThrowIfCancellationRequested();
        long request;
        lock (_sync) request = ++_latestSeek;
        Task operation = SeekCoreAsync(seconds, request, cancellation);
        // Если вызывающий код прекратил ждать, нативный переход всё равно завершается
        // до допуска следующего seek. Его ошибка уже записывается в Fault.
        _ = operation.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await operation.WaitAsync(cancellation);
    }

    private async Task SeekCoreAsync(double seconds, long request, CancellationToken cancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        await _operation.WaitAsync(linked.Token);
        try
        {
            ThrowIfUnavailable(requireOpen: true);
            lock (_sync)
            {
                // До отправки команды устаревший запрос можно безопасно пропустить.
                if (request != _latestSeek) throw new OperationCanceledException("Seek superseded by a newer target.");
                cancellation.ThrowIfCancellationRequested();
                _ended = false;
            }
            Task ready = BeginRestartWait(20);
            try
            {
                // PLAYBACK_RESTART не содержит request id. Поэтому отмена ожидания
                // не освобождает сериализатор до собственного command reply + restart.
                await CommandAsync(["seek", seconds.ToString("R", CultureInfo.InvariantCulture), "absolute+exact"], _lifetime.Token);
                await ready.WaitAsync(_lifetime.Token);
                SetEvidence("absolute+exact seek; MPV_EVENT_SEEK → MPV_EVENT_PLAYBACK_RESTART; API restart complete, presentation checked externally");
            }
            finally { ClearRestartWait(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { RecordOperationFailure(error); throw; }
        finally { _operation.Release(); }
    }

    public async Task PlayAsync(CancellationToken cancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        await _operation.WaitAsync(linked.Token);
        try
        {
            ThrowIfUnavailable(requireOpen: true);
            lock (_sync)
            {
                if (_ended) throw new InvalidOperationException("mpv reached EOF; seek before playing.");
            }
            await CommandAsync(["set", "pause", "no"], linked.Token);
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    ThrowIfUnavailable(requireOpen: true);
                    if (_ended) throw new EndOfStreamException("mpv reached EOF before playback became active.");
                    changed = _coreStateChanged.Task;
                }
                // Свежий async query после command reply исключает старое событие pause.
                // core-idle=false означает активный playback; первый кадр готов после restart.
                if (!await QueryFlagAsync("core-idle", linked.Token)) break;
                await changed.WaitAsync(linked.Token);
            }
            SetEvidence("unpause command acknowledged; fresh core-idle=false after completed PLAYBACK_RESTART; playback core active, presentation checked externally");
        }
        catch (Exception error) { RecordOperationFailure(error); throw; }
        finally { _operation.Release(); }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed || _api is null || _handle == 0) return;
            // Ответ также обрабатывается циклом событий, но синхронный интерфейс Pause его не ожидает.
            int result = _api.Command(_handle, 0, ["set", "pause", "yes"]);
            if (result < 0) FailLocked(new InvalidOperationException(_api.Error(result)));
        }
    }

    public async Task PauseAsync(bool paused, CancellationToken cancellation)
    {
        if (!paused) { await PlayAsync(cancellation); return; }
        await CommandAsync(["set", "pause", "yes"], cancellation);
    }
    public async Task SaveFrameAsync(string file, CancellationToken cancellation)
        => await CommandAsync(["screenshot-to-file", file, "video"], cancellation);

    private void Initialize(nint surface)
    {
        try { InitializeCore(surface); }
        catch (Exception error) when (error is not MpvInfrastructureException)
        {
            throw new MpvInfrastructureException($"mpv infrastructure initialization: {error.Message}", error);
        }
    }

    private void InitializeCore(nint surface)
    {
        string dll = Environment.GetEnvironmentVariable("REWIND_MPV_DLL") ?? Environment.GetEnvironmentVariable("REWIND_LAB_MPV_DLL") ?? Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll");
        if (!Path.IsPathFullyQualified(dll) || !File.Exists(dll)) throw new FileNotFoundException("Не найдена библиотека видеоплеера mpv.", dll);
        string cache = Path.Combine(Path.GetFullPath(cacheRoot), "mpv");
        Directory.CreateDirectory(cache);
        MpvApi api = new(dll);
        nint handle = api.Create();
        if (handle == 0) { api.Dispose(); throw new InvalidOperationException("mpv_create failed."); }
        try
        {
            (string Name, string Value)[] settings =
            [
                ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"),
                ("terminal", "no"), ("input-default-bindings", "no"), ("input-vo-keyboard", "no"),
                ("osc", "no"), ("osd-level", "0"), ("audio", nativeAudio ? "auto" : "no"), ("sub", "no"),
                ("idle", "yes"), ("keep-open", "yes"), ("pause", "yes"),
                ("wid", surface.ToInt64().ToString(CultureInfo.InvariantCulture)),
                ("vo", headless ? "null" : "gpu-next"), ("gpu-api", "d3d11"), ("gpu-context", "d3d11"),
                ("hwdec", software ? "no" : "auto-safe"), ("video-latency-hacks", "no"),
                // Названия проверены по options.rst и таблицам опций commit 0fae46954.
                // Дисковый packet cache отключён; shader/ICC cache остаются в sandbox.
                ("cache-on-disk", "no"), ("demuxer-cache-dir", cache),
                ("gpu-shader-cache-dir", cache), ("icc-cache-dir", cache)
            ];
            foreach ((string name, string value) in settings) api.Check(api.SetOption(handle, name, value), $"option {name}");
            api.Check(api.Initialize(handle), "mpv_initialize");
            api.Check(api.Observe(handle, 1, "time-pos", 5), "observe time-pos");
            api.Check(api.Observe(handle, 2, "core-idle", 3), "observe core-idle");
            api.Check(api.Observe(handle, 3, "eof-reached", 3), "observe eof-reached");
            api.Check(api.Observe(handle, 4, "frame-drop-count", 4), "observe frame-drop-count");
            api.Check(api.Observe(handle, 5, "decoder-frame-drop-count", 4), "observe decoder-frame-drop-count");
            lock (_sync) { _api = api; _handle = handle; }
            _events = Task.Factory.StartNew(EventLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { api.Destroy(handle); api.Dispose(); throw; }
    }

    private Task BeginRestartWait(int expectedEvent)
    {
        lock (_sync)
        {
            _expectedEvent = expectedEvent;
            _restartArmed = false;
            _restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _restart.Task;
        }
    }

    private void ClearRestartWait() { lock (_sync) { _restart = null; _restartArmed = false; } }
    private void SetEvidence(string evidence) { lock (_sync) _evidence = evidence; }

    private async Task<bool> QueryFlagAsync(string property, CancellationToken cancellation)
    {
        ulong id;
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ThrowIfUnavailable(requireOpen: true);
            id = ++_nextCommand;
            _flagQueries.Add(id, completion);
            int result = _api!.GetPropertyAsync(_handle, id, property, 3);
            if (result < 0) { _flagQueries.Remove(id); throw new InvalidOperationException(_api.Error(result)); }
        }
        try { return await completion.Task.WaitAsync(cancellation); }
        finally { lock (_sync) _flagQueries.Remove(id); }
    }

    private async Task CommandAsync(string[] arguments, CancellationToken cancellation)
    {
        ulong id;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ThrowIfUnavailable(requireOpen: true);
            id = ++_nextCommand;
            _commands.Add(id, completion);
            int result = _api!.Command(_handle, id, arguments);
            if (result < 0) { _commands.Remove(id); throw new InvalidOperationException(_api.Error(result)); }
        }
        try { await completion.Task.WaitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            lock (_sync) { if (_handle != 0) _api!.Abort(_handle, id); }
            throw;
        }
        finally { lock (_sync) _commands.Remove(id); }
    }

    private void EventLoop()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                MpvEvent current = Marshal.PtrToStructure<MpvEvent>(_api!.WaitEvent(_handle, -1));
                lock (_sync)
                {
                    if (_disposed) return;
                    if (current.Id == _expectedEvent) _restartArmed = true;
                    switch (current.Id)
                    {
                        case 1: FailLocked(new InvalidOperationException("mpv shutdown.")); return;
                        case 3:
                            if (_flagQueries.TryGetValue(current.UserData, out TaskCompletionSource<bool>? query))
                            {
                                if (current.Error < 0) query.TrySetException(new InvalidOperationException(_api.Error(current.Error)));
                                else
                                {
                                    MpvProperty property = Marshal.PtrToStructure<MpvProperty>(current.Data);
                                    if (property.Format == 3 && property.Data != 0) query.TrySetResult(Marshal.ReadInt32(property.Data) != 0);
                                    else query.TrySetException(new InvalidOperationException("mpv flag query returned an unavailable or non-flag value."));
                                }
                            }
                            break;
                        case 5:
                            if (_commands.TryGetValue(current.UserData, out TaskCompletionSource? completion))
                            {
                                if (current.Error < 0) completion.TrySetException(new InvalidOperationException(_api.Error(current.Error)));
                                else completion.TrySetResult();
                            }
                            else if (current.Error < 0) FailLocked(new InvalidOperationException(_api.Error(current.Error)));
                            break;
                        case 7:
                            MpvEndFile end = Marshal.PtrToStructure<MpvEndFile>(current.Data);
                            if (end.Reason == 4) FailLocked(new InvalidOperationException($"mpv playback error: {_api.Error(end.Error)}"));
                            else if (end.Reason == 0)
                            {
                                _ended = true;
                                FailPendingLocked(new EndOfStreamException("mpv reached EOF before the pending operation completed."));
                            }
                            break;
                        case 21:
                            if (_restartArmed) _restart?.TrySetResult();
                            break;
                        case 22: ReadProperty(current); break;
                        case 24: FailLocked(new InvalidOperationException("mpv event queue overflow; readiness evidence lost.")); break;
                    }
                }
            }
        }
        catch (Exception error) { lock (_sync) FailLocked(error); }
    }

    private void ReadProperty(MpvEvent current)
    {
        MpvProperty property = Marshal.PtrToStructure<MpvProperty>(current.Data);
        bool available = property.Format != 0 && property.Data != 0;
        switch (current.UserData)
        {
            case 1:
                if (available && property.Format == 5)
                    _position = Marshal.PtrToStructure<double>(property.Data);
                break;
            case 2:
                _coreStateChanged.TrySetResult();
                _coreStateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                break;
            case 3:
                if (available && property.Format == 3)
                {
                    _ended = Marshal.ReadInt32(property.Data) != 0;
                    if (_ended) FailPendingLocked(new EndOfStreamException("mpv reached EOF before the pending operation completed."));
                }
                break;
            case 4: _voDropped = available && property.Format == 4 ? Marshal.ReadInt64(property.Data) : null; break;
            case 5: _decoderDropped = available && property.Format == 4 ? Marshal.ReadInt64(property.Data) : null; break;
        }
    }

    private void RecordOperationFailure(Exception error)
    {
        lock (_sync)
        {
            // Нативный seek имеет собственную обработку отмены и удерживает сериализатор.
            // Здесь остаются ошибки загрузки, Play и собственно нативных переходов.
            if (!_disposed) FailLocked(error);
        }
    }

    private void FailLocked(Exception error) { _fault ??= error.Message; FailPendingLocked(error); }
    private void FailPendingLocked(Exception error)
    {
        _restart?.TrySetException(error);
        _coreStateChanged.TrySetException(error);
        foreach (TaskCompletionSource completion in _commands.Values) completion.TrySetException(error);
        foreach (TaskCompletionSource<bool> completion in _flagQueries.Values) completion.TrySetException(error);
    }

    private void ThrowIfUnavailable(bool requireOpen = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null) throw new InvalidOperationException(_fault);
            if (requireOpen && _handle == 0) throw new InvalidOperationException("mpv is not initialized.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            FailPendingLocked(new ObjectDisposedException(nameof(MpvVideo)));
            if (_handle != 0) _api!.Wakeup(_handle);
        }
        await _operation.WaitAsync();
        try
        {
            if (_events is not null) await _events;
            MpvApi? api;
            nint handle;
            lock (_sync) { api = _api; handle = _handle; _handle = 0; _api = null; }
            if (api is not null)
            {
                // mpv_terminate_destroy может ждать нативные потоки; watchdog стенда ограничивает процесс.
                await Task.Run(() => { if (handle != 0) api.Destroy(handle); api.Dispose(); });
            }
        }
        finally { _operation.Release(); }
    }

    // ABI: https://github.com/mpv-player/mpv/blob/master/include/mpv/client.h
    // Семантика событий и свойств: https://mpv.io/manual/master/#command-interface
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent { public int Id; public int Error; public ulong UserData; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvProperty { public nint Name; public int Format; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEndFile { public int Reason; public int Error; }

    private sealed class MpvApi : IDisposable
    {
        private readonly nint _library;
        internal readonly CreateDelegate Create;
        internal readonly HandleResultDelegate Initialize;
        internal readonly HandleVoidDelegate Destroy;
        internal readonly HandleVoidDelegate Wakeup;
        internal readonly AbortDelegate Abort;
        internal readonly OptionDelegate SetOption;
        internal readonly ObserveDelegate Observe;
        internal readonly ObserveDelegate GetPropertyAsync;
        internal readonly WaitDelegate WaitEvent;
        private readonly CommandDelegate _command;
        private readonly ErrorDelegate _error;

        internal MpvApi(string path)
        {
            _library = NativeLibrary.Load(path);
            try
            {
                Create = Bind<CreateDelegate>("mpv_create");
                Initialize = Bind<HandleResultDelegate>("mpv_initialize");
                Destroy = Bind<HandleVoidDelegate>("mpv_terminate_destroy");
                Wakeup = Bind<HandleVoidDelegate>("mpv_wakeup");
                Abort = Bind<AbortDelegate>("mpv_abort_async_command");
                SetOption = Bind<OptionDelegate>("mpv_set_option_string");
                Observe = Bind<ObserveDelegate>("mpv_observe_property");
                GetPropertyAsync = Bind<ObserveDelegate>("mpv_get_property_async");
                WaitEvent = Bind<WaitDelegate>("mpv_wait_event");
                _command = Bind<CommandDelegate>("mpv_command_async");
                _error = Bind<ErrorDelegate>("mpv_error_string");
            }
            catch { NativeLibrary.Free(_library); throw; }
        }

        private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        internal string Error(int code) => Marshal.PtrToStringUTF8(_error(code)) ?? $"mpv error {code}";
        internal void Check(int result, string operation) { if (result < 0) throw new InvalidOperationException($"{operation}: {Error(result)}"); }

        internal int Command(nint handle, ulong id, string[] arguments)
        {
            nint[] strings = new nint[arguments.Length];
            nint array = Marshal.AllocHGlobal((arguments.Length + 1) * nint.Size);
            try
            {
                for (int index = 0; index < arguments.Length; index++)
                {
                    strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                    Marshal.WriteIntPtr(array, index * nint.Size, strings[index]);
                }
                Marshal.WriteIntPtr(array, arguments.Length * nint.Size, 0);
                return _command(handle, id, array);
            }
            finally
            {
                foreach (nint value in strings) if (value != 0) Marshal.FreeCoTaskMem(value);
                Marshal.FreeHGlobal(array);
            }
        }

        public void Dispose() => NativeLibrary.Free(_library);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint CreateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int HandleResultDelegate(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void HandleVoidDelegate(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void AbortDelegate(nint handle, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OptionDelegate(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ObserveDelegate(nint handle, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint WaitDelegate(nint handle, double timeout);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandDelegate(nint handle, ulong id, nint arguments);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorDelegate(int code);
    }
}

// Ошибка загрузки DLL/ABI/опций стенда не является результатом качества движка.
public sealed class MpvInfrastructureException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed record MpvCounters(long? Decoded, long? Displayed, long? Dropped);
