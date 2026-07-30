using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AirBlade
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // 导入 Rust 导出的 C-ABI 函数
        [DllImport("airplay_core.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "airplay_core_init")]
        private static extern int AirPlayCoreInit();
        public MainWindow()
        {
            InitializeComponent();
        }
        private void myButton_Click(object sender, RoutedEventArgs e)
        {
            // 点击按钮时调用 Rust 核心引擎
            int result = AirPlayCoreInit();

            myButton.Content = result == 0
                ? "Rust Core Initialized Successfully!"
                : $"Failed with code: {result}";
        }
    }
}
