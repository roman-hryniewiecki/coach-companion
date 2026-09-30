using CouchCompanion.Models;
using CouchCompanion.ViewModels;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Forms;

namespace CouchCompanion
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        private MainViewModel _viewModel;

        public MainWindow()
        {
            Console.WriteLine("MainWindow constructor started.");
            InitializeComponent();
            _viewModel = new MainViewModel();
            this.DataContext = _viewModel;

            this.Closed += MainWindow_Closed;
            Console.WriteLine("MainWindow constructor finished.");
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("MainWindow Window_Loaded event started.");
            _viewModel.RefreshCommand.Execute(null);
            DockToRightEdge();
            Console.WriteLine("MainWindow Window_Loaded event finished.");
        }

        private void ScreenComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DockToRightEdge();
        }


        private void DockToRightEdge()
        {
            if (_viewModel.SelectedScreen != null)
            {
                ScreenInfo selectedScreen = _viewModel.SelectedScreen;

                // Calculate the new position for the main window
                this.Left = selectedScreen.BoundsX + selectedScreen.BoundsWidth - this.ActualWidth;
                this.Top = selectedScreen.BoundsY;
                this.Height = selectedScreen.BoundsHeight;

                // Position the selected window to the left of the app
                PositionSelectedWindow();
            }
        }

        private void PositionSelectedWindow()
        {
            if (_viewModel.SelectedWindow != null && _viewModel.SelectedScreen != null)
            {
                ScreenInfo selectedScreen = _viewModel.SelectedScreen;
                WindowInfo selectedWindow = _viewModel.SelectedWindow;

                // Calculate the new position and size for the selected window
                int newLeft = selectedScreen.BoundsX;
                int newTop = selectedScreen.BoundsY;
                int newWidth = (int)(selectedScreen.BoundsWidth - this.ActualWidth);
                int newHeight = selectedScreen.BoundsHeight;

                // Set the position and size of the selected window
                SetWindowPos(selectedWindow.Hwnd, IntPtr.Zero, newLeft, newTop, newWidth, newHeight, SWP_NOZORDER | SWP_SHOWWINDOW);
            }
        }

        // P/Invoke declarations for window positioning
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public void Dispose()
        {
            _viewModel?.Dispose();
        }
    }
}
