using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using RTSPCaster.ViewModels;

namespace RTSPCaster
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.Logs.CollectionChanged += Logs_CollectionChanged;
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