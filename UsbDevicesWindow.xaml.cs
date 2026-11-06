using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace AB
{
	public partial class UsbDevicesWindow : Window
	{
		private readonly MainWindow _owner;
		private List<UsbDeviceRow> _allRows = new List<UsbDeviceRow>();

		public UsbDevicesWindow(MainWindow owner)
		{
			InitializeComponent();
			_owner = owner;
			Owner = owner;
			Loaded += (s, e) => RefreshList();
		}

		private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.F5)
			{
				RefreshList();
				e.Handled = true;
			}
		}

		private void BtnRefresh_Click(object sender, RoutedEventArgs e)
		{
			RefreshList();
		}

		private void FilterChanged(object sender, RoutedEventArgs e)
		{
			ApplyFilter();
		}

		private void BtnClose_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			ApplySelected();
		}

		private void BtnApply_Click(object sender, RoutedEventArgs e)
		{
			ApplySelected();
		}

		private void ApplySelected()
		{
			if (DeviceList.SelectedItem is UsbDeviceRow row)
			{
				_owner.ApplyCustomVidPid(row.Vid, row.Pid);
				Close();
			}
		}

		private async void BtnDelete_Click(object sender, RoutedEventArgs e)
		{
			List<UsbDeviceRow> selected = DeviceList.SelectedItems.Cast<UsbDeviceRow>().ToList();
			if (selected.Count == 0)
			{
				MessageBox.Show("请先选择要删除的 VID/PID 记录。", "USB 设备", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			if (!IsAdministrator())
			{
				MessageBoxResult elevate = MessageBox.Show(
					"删除以前的 USB VID/PID 记录需要管理员权限。是否以管理员身份重新启动本程序？",
					"需要管理员权限",
					MessageBoxButton.YesNo,
					MessageBoxImage.Warning);
				if (elevate == MessageBoxResult.Yes)
				{
					RestartAsAdministrator();
				}
				return;
			}

			bool anyConnected = selected.Any(r => r.Connected);
			string names = string.Join("\n", selected.Select(r => r.Vid + ":" + r.Pid + "  " + r.Name));
			string warn = anyConnected
				? "选中的记录里包含当前已连接的设备，删除后设备会断开并清除系统里的 VID/PID 记录。\n\n"
				: "将从系统中删除以下已记录的 VID/PID（包括以前插过、现在已拔掉的设备）。下次插入会重新识别。\n\n";
			MessageBoxResult confirm = MessageBox.Show(
				warn + names + "\n\n确定删除？",
				"删除 USB 记录",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning);
			if (confirm != MessageBoxResult.Yes)
			{
				return;
			}

			List<string> instanceIds = selected.SelectMany(r => r.InstanceIds ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			BtnDelete.IsEnabled = false;
			try
			{
				string result = await Task.Run(() => UsbDeviceSetup.UninstallInstances(instanceIds));
				MessageBox.Show(result, "删除 USB 记录", MessageBoxButton.OK, MessageBoxImage.Information);
				RefreshList();
			}
			catch (Exception ex)
			{
				MessageBox.Show("删除失败：" + ex.Message, "删除 USB 记录", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				BtnDelete.IsEnabled = true;
			}
		}

		private async void RefreshList()
		{
			BtnRefresh.IsEnabled = false;
			TxtCount.Text = "正在扫描 USB 设备...";
			try
			{
				List<UsbDeviceRow> rows = await Task.Run(() => BuildRows());
				_allRows = rows;
				ApplyFilter();
			}
			catch (Exception ex)
			{
				TxtCount.Text = "扫描失败：" + ex.Message;
			}
			finally
			{
				BtnRefresh.IsEnabled = true;
			}
		}

		private void ApplyFilter()
		{
			if (DeviceList == null)
			{
				return;
			}
			bool connectedOnly = ChkConnectedOnly != null && ChkConnectedOnly.IsChecked == true;
			bool vid38A6Only = ChkVid38A6 != null && ChkVid38A6.IsChecked == true;
			List<UsbDeviceRow> view = _allRows
				.Where(r => (!connectedOnly || r.Connected) && (!vid38A6Only || r.Vid == "38A6"))
				.ToList();
			DeviceList.ItemsSource = view;
			UsbDeviceRow preferred = view.FirstOrDefault(r => !r.Connected && r.Vid == "38A6")
				?? view.FirstOrDefault(r => r.Connected && r.Vid == "38A6")
				?? view.FirstOrDefault();
			if (preferred != null)
			{
				DeviceList.SelectedItem = preferred;
				DeviceList.ScrollIntoView(preferred);
			}
			int disconnected = view.Count(r => !r.Connected);
			TxtCount.Text = string.Format(CultureInfo.InvariantCulture, "共 {0} 组，其中历史记录 {1} 组", view.Count, disconnected);
		}

		private static List<UsbDeviceRow> BuildRows()
		{
			var map = new Dictionary<string, UsbDeviceRow>(StringComparer.OrdinalIgnoreCase);
			foreach (UsbDeviceSetup.DeviceRecord rec in UsbDeviceSetup.EnumerateUsbRecords())
			{
				string key = rec.Vid + ":" + rec.Pid;
				UsbDeviceRow row;
				if (!map.TryGetValue(key, out row))
				{
					row = new UsbDeviceRow
					{
						Name = rec.Name,
						Vid = rec.Vid,
						Pid = rec.Pid,
						Connected = rec.Connected,
						Status = rec.Connected ? "已连接" : "历史",
						InstanceIds = new List<string>()
					};
					map[key] = row;
				}
				row.Connected = row.Connected || rec.Connected;
				row.Status = row.Connected ? "已连接" : "历史";
				if (IsBetterName(rec.Name, row.Name))
				{
					row.Name = rec.Name;
				}
				if (!string.IsNullOrEmpty(rec.InstanceId) &&
					!row.InstanceIds.Contains(rec.InstanceId, StringComparer.OrdinalIgnoreCase))
				{
					row.InstanceIds.Add(rec.InstanceId);
				}
			}
			return map.Values
				.OrderBy(r => r.Connected)
				.ThenByDescending(r => r.Vid == "38A6")
				.ThenBy(r => r.Vid, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.Pid, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private static bool IsBetterName(string candidate, string current)
		{
			if (string.IsNullOrWhiteSpace(current))
			{
				return true;
			}
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return false;
			}
			return NameScore(candidate) > NameScore(current);
		}

		private static int NameScore(string name)
		{
			string n = name.Trim();
			if (n.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("复合"))
			{
				return 0;
			}
			if (n.IndexOf("Generic USB Hub", StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("集线器"))
			{
				return 1;
			}
			if (n.IndexOf("USB Input Device", StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("输入设备"))
			{
				return 2;
			}
			if (n.IndexOf("USB Audio", StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("音频"))
			{
				return 3;
			}
			return 10;
		}

		private static bool IsAdministrator()
		{
			try
			{
				WindowsIdentity identity = WindowsIdentity.GetCurrent();
				return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
			}
			catch
			{
				return false;
			}
		}

		private void RestartAsAdministrator()
		{
			try
			{
				ProcessStartInfo info = new ProcessStartInfo
				{
					FileName = Assembly.GetExecutingAssembly().Location,
					UseShellExecute = true,
					Verb = "runas"
				};
				Process.Start(info);
				Application.Current.Shutdown();
			}
			catch
			{
				MessageBox.Show("未能切换到管理员权限，请右键本程序选择「以管理员身份运行」。", "USB 设备", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}

		public sealed class UsbDeviceRow
		{
			public string Name { get; set; }
			public string Vid { get; set; }
			public string Pid { get; set; }
			public string Status { get; set; }
			public bool Connected { get; set; }
			public List<string> InstanceIds { get; set; }
		}
	}
}
