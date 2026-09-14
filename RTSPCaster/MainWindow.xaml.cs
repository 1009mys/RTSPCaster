using System.Collections.Specialized;
using System.Windows;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using RTSPCaster.ViewModels;

namespace RTSPCaster
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand CopyLogsCommand = new("CopyLogs", "CopyLogs", typeof(MainWindow));
        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.Logs.CollectionChanged += Logs_CollectionChanged;
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCopyLogs();
        }

        private void CopyLogs_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ExecuteCopyLogs();
        }

        private void ExecuteCopyLogs()
        {
            // 선택된 항목이 있으면 선택된 것들을, 없으면 전체 로그를 복사
            var itemsToCopy = LogList.SelectedItems.Count > 0
                ? LogList.SelectedItems.Cast<object>().Select(o => o.ToString())
                : LogList.Items.Cast<object>().Select(o => o.ToString());

            var text = string.Join(System.Environment.NewLine, itemsToCopy.Where(s => s != null));
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // 클립보드 접근 실패 시 조용히 무시 (필요시 사용자에게 알려주는 로직 추가 가능)
                }
            }
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;

            // ItemContainerGenerator가 컬렉션 변경을 반영한 뒤에 스크롤하도록 지연.
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (LogList.Items.Count == 0) return;
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]!);
            }), DispatcherPriority.Background);
        }
    }
}