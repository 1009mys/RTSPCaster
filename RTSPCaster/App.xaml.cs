using System.Windows;
using RTSPCaster.Services;
using RTSPCaster.ViewModels;

namespace RTSPCaster
{
    public partial class App : Application
    {
        private ChildProcessTracker? _tracker;
        private SqliteService? _db;
        private StreamingService? _streaming;
        private MainViewModel? _vm;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _tracker = new ChildProcessTracker();
            _db = new SqliteService();
            var probe = new FfprobeService();
            var conversion = new ConversionService(_db, _tracker);
            _streaming = new StreamingService(_db, _tracker);

            _vm = new MainViewModel(_db, probe, conversion, _streaming);

            // 외부에서 실행 중인 MediaMTX 인스턴스에만 연결한다.
            _vm.DetectExternalMediaMtx();

            var window = new MainWindow(_vm);
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _vm?.ShutdownAll(); } catch { }
            try { _streaming?.Dispose(); } catch { }
            try { _tracker?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
