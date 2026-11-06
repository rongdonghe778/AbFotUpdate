using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AB
{
	public partial class App : Application
	{
		private Mutex _singleInstanceMutex;

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		private const int SW_RESTORE = 9;

		protected override void OnStartup(StartupEventArgs e)
		{
			bool createdNew;
			_singleInstanceMutex = new Mutex(true, @"Local\CM2206.AbFotUpdate.SingleInstance", out createdNew);
			if (!createdNew)
			{
				ActivateExistingWindow();
				Shutdown();
				return;
			}

			base.OnStartup(e);
		}

		protected override void OnExit(ExitEventArgs e)
		{
			if (_singleInstanceMutex != null)
			{
				try
				{
					_singleInstanceMutex.ReleaseMutex();
				}
				catch
				{
				}
				_singleInstanceMutex.Dispose();
				_singleInstanceMutex = null;
			}
			base.OnExit(e);
		}

		private static void ActivateExistingWindow()
		{
			IntPtr hwnd = FindWindow(null, "CM2206 固件升级");
			if (hwnd == IntPtr.Zero)
			{
				return;
			}
			ShowWindow(hwnd, SW_RESTORE);
			SetForegroundWindow(hwnd);
		}
	}
}
