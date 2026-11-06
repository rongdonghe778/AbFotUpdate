using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Diagnostics;
using AB.fota;
using AB;
using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.Windows.Threading;
using System.Management;
using System.Timers;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AB.Properties;
using System.ComponentModel;
using HidLibrary;
using System.Reflection;

namespace AB
{
	/// <summary>
	/// MainWindow.xaml 的交互逻辑
	/// </summary>
	public partial class MainWindow : Window
	{
		#region DllImport 及 Win32 常量定义
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateFileW(
			string lpFileName,                // 设备路径（如\\?\hid#vid_0642&pid_0001#...）
			uint dwDesiredAccess,             // 访问权限
			uint dwShareMode,                 // 共享模式
			IntPtr lpSecurityAttributes,      // 安全属性（null）
			uint dwCreationDisposition,       // 创建方式（OPEN_EXISTING）
			uint dwFlagsAndAttributes,        // 标志（重叠IO）
			IntPtr hTemplateFile              // 模板文件（null）
		);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

		// CreateFileW 常量定义
		private const uint GENERIC_READ = 0x80000000;
		private const uint GENERIC_WRITE = 0x40000000;
		private const uint FILE_SHARE_READ = 0x00000001;
		private const uint FILE_SHARE_WRITE = 0x00000002;
		private const uint OPEN_EXISTING = 3;
		private const uint FILE_FLAG_OVERLAPPED = 0x40000000; // 重叠IO（异步）
		private const int INVALID_HANDLE_VALUE = -1;
		#endregion

		#region 核心常量
		private int VendorId;
		private int ProductId;
		private const ushort UsagePage = 0xFFAB; //65451
		private const byte ReportId = 0xAB;
		private const int PacketSize = 64;
		#endregion

		#region 成员变量（替换HidLibrary，整合新HID实现）
		// 业务逻辑变量
		private OtaManager otaManager;
		private OtaManager.MyEventListener listener = new OtaManager.MyEventListener();
		public HashSet<string> loggedMessages = new HashSet<string>(); // 使用HashSet存储已记录的消息
		private Dictionary<string, bool> deviceStatus = new Dictionary<string, bool>(); // 记录设备插拔状态
		private int version = 0xFF;
		private bool _isFileReady = false;
		private string _firmwareGuidPayload;

		// 设备监听与防抖
		private ManagementEventWatcher insertWatcher;
		private ManagementEventWatcher removeWatcher;
		private Timer removeDebounceTimer;
		private const int DebounceInterval = 500;

		// 新HID实现变量
		public bool _isDeviceReady = false;
		private string _selectedDevicePath;
		public FileStream _hidFileStream;
		private IntPtr _hidDeviceHandle = IntPtr.Zero;
		private byte[] _readBuffer;
		private bool _isReading = false;
		private volatile bool _isClosing;
		private volatile bool _disposingHid;
		private SafeFileHandle _safeHidHandle;
		#endregion

		public MainWindow()
		{
			InitializeComponent();
			InitTitleVersion();
			InitProductComboBox();
			StateChanged += (s, e) =>
			{
				if (BtnCaptionMax != null)
				{
					BtnCaptionMax.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
				}
			};
			// 初始化业务逻辑
			otaManager = new OtaManager(listener, this);
			BtnSendData.IsEnabled = false;
			InitializeDeviceEventWatchers();

			// 初始化防抖定时器
			removeDebounceTimer = new Timer(DebounceInterval);
			removeDebounceTimer.Elapsed += RemoveDebounceTimerElapsed;
			removeDebounceTimer.AutoReset = false;

			// 初始化新HID读取缓冲区
			_readBuffer = new byte[PacketSize];

			// 窗口真正显示后再自动扫描，避免启动瞬间 HID 还未就绪
			ContentRendered += OnWindowContentRendered;
		}

		#region 产品选择
		private bool _suppressProductAutoConnect;
		private bool _suppressVidPidLog;
		private bool _isFindingDevice;
		private int _pendingProductId;
		private string _detectedProductName;

		private sealed class KnownDongle
		{
			public string Name;
			public string VidHex;
			public int Vid;
			public string PidHex;
			public int Pid;
		}

		private sealed class HidChoice
		{
			public HidDevice Device { get; set; }
			public string ProductName { get; set; }
			public int VendorId { get; set; }
			public int ProductId { get; set; }
			public string Display { get; set; }
		}

		private static readonly KnownDongle[] KnownDongles =
		{
			new KnownDongle { Name = "A7 MAX", VidHex = "38A6", Vid = 0x38A6, PidHex = "001A", Pid = 0x001A },
			new KnownDongle { Name = "A10", VidHex = "38A6", Vid = 0x38A6, PidHex = "001D", Pid = 0x001D },
			new KnownDongle { Name = "CM2206", VidHex = "0D8C", Vid = 0x0D8C, PidHex = "0352", Pid = 0x0352 }
		};

		private void InitProductComboBox()
		{
			ProductComboBox.Items.Add(new ComboBoxItem { Content = "自动识别", Tag = "AUTO" });
			ProductComboBox.Items.Add(new ComboBoxItem { Content = "自定义", Tag = "CUSTOM" });
			ProductComboBox.SelectionChanged += ProductComboBox_SelectionChanged;
			ProductComboBox.DropDownOpened += (s, e) => ApplyProductDisplayOverlay();
			ProductComboBox.DropDownClosed += (s, e) => ApplyProductDisplayOverlay();
			_suppressProductAutoConnect = true;
			ProductComboBox.SelectedIndex = 0;
			_suppressProductAutoConnect = false;
			SetVidPidEditable(false);
			SetProductDisplayOverlay(null);
		}

		private void OnWindowContentRendered(object sender, EventArgs e)
		{
			ContentRendered -= OnWindowContentRendered;
			InitializeInfo();
			ProgressBarOta.ValueChanged += (ps, pe) =>
			{
				if (_TextPercent != null)
				{
					_TextPercent.Text = string.Format("{0:0}%", ProgressBarOta.Value);
				}
			};
			LogMessage("CM2206 固件升级：启动后自动识别 Dongle");
			HookPlaceholderCommandBoxes();
			TryRestoreLastFirmware();
			StartStartupDongleScan();
		}

		private void StartStartupDongleScan()
		{
			SetDeviceStatus("正在自动识别...", false);
			FindAndConnectDongle(forceAuto: true);
			if (_isDeviceReady)
			{
				return;
			}

			DispatcherTimer retryTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(800)
			};
			retryTimer.Tick += (s, e) =>
			{
				retryTimer.Stop();
				if (!_isDeviceReady)
				{
					FindAndConnectDongle(forceAuto: true, quiet: true);
				}
			};
			retryTimer.Start();
		}

		private void ProductComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (infoVid == null || infoPid == null)
			{
				return;
			}
			if (ProductComboBox.SelectedItem is ComboBoxItem item && item.Tag is string pid)
			{
				bool custom = string.Equals(pid, "CUSTOM", StringComparison.OrdinalIgnoreCase);
				SetVidPidEditable(custom);
				if (!custom)
				{
					_detectedProductName = null;
				}
				ApplyProductDisplayOverlay();
				SaveLastProductPid(pid);
				if (!_suppressProductAutoConnect && IsLoaded)
				{
					if (IsOtaBusy())
					{
						LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再切换产品");
						return;
					}
					DisposeHidResources();
					_selectedDevicePath = null;
					BtnFindDevice_Click(null, null);
				}
			}
		}

		private bool IsAutoProductMode()
		{
			return (ProductComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "AUTO";
		}

		private void SetVidPidEditable(bool editable)
		{
			if (infoVid == null || infoPid == null)
			{
				return;
			}
			infoVid.IsReadOnly = !editable;
			infoPid.IsReadOnly = !editable;
			Brush background = new SolidColorBrush(editable
				? Colors.White
				: Color.FromRgb(0xF7, 0xF9, 0xFB));
			infoVid.Background = background;
			infoPid.Background = background;
		}

		private void SetProductDisplayOverlay(string productName)
		{
			_detectedProductName = productName;
			ApplyProductDisplayOverlay();
		}

		private void ApplyProductDisplayOverlay()
		{
			if (ProductDisplayOverlay == null || ProductComboBox == null)
			{
				return;
			}
			if (ProductComboBox.IsDropDownOpen ||
				string.IsNullOrEmpty(_detectedProductName) ||
				!IsAutoProductMode())
			{
				ProductDisplayOverlay.Visibility = Visibility.Collapsed;
				ProductComboBox.ClearValue(Control.ForegroundProperty);
				return;
			}
			ProductDisplayOverlay.Text = _detectedProductName;
			ProductDisplayOverlay.Visibility = Visibility.Visible;
			ProductComboBox.Foreground = Brushes.Transparent;
		}
		#endregion

		#region 原有初始化逻辑（适配新HID实现）
		private void InitializeInfo()
		{
			string vidText = infoVid.Text?.Trim() ?? "";
			string pidText = infoPid.Text?.Trim() ?? "";

			if (string.IsNullOrEmpty(vidText))
			{
				VendorId = 0;
			}
			else if (int.TryParse(vidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int vendorId))
			{
				VendorId = vendorId;
			}
			else
			{
				TxtLog.AppendText($"错误：VID格式无效！请输入十六进制值（例如0642），当前输入：{vidText}\r\n");
				VendorId = 0;
			}

			if (string.IsNullOrEmpty(pidText))
			{
				ProductId = 0;
			}
			else if (int.TryParse(pidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int productId))
			{
				ProductId = productId;
			}
			else
			{
				TxtLog.AppendText($"错误：PID格式无效！请输入十六进制值（例如0001），当前输入：{pidText}\r\n");
				ProductId = 0;
			}

			infoVid.TextChanged += InfoVid_TextChanged;
			infoPid.TextChanged += InfoPid_TextChanged;
		}

		private void InitializeDeviceEventWatchers()
		{
			// 监听设备插入事件
			WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2");
			insertWatcher = new ManagementEventWatcher(insertQuery);
			insertWatcher.EventArrived += new EventArrivedEventHandler(DeviceInserted);
			insertWatcher.Start();

			// 监听设备移除事件
			WqlEventQuery removeQuery = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 3");
			removeWatcher = new ManagementEventWatcher(removeQuery);
			removeWatcher.EventArrived += new EventArrivedEventHandler(DeviceRemovedDebounced);
			removeWatcher.Start();
		}
		#endregion

		#region 设备插拔逻辑（适配新HID资源释放）
		private void DeviceInserted(object sender, EventArrivedEventArgs e)
		{
			if (_isClosing)
			{
				return;
			}
			Dispatcher.InvokeAsync(() =>
			{
				if (_isClosing || _isDeviceReady || (otaManager != null && otaManager.IsUpdating()) || _isFindingDevice)
				{
					return;
				}
				LogMessage("检测到设备插入，正在自动识别...");
				BtnFindDevice_Click(null, null);
			});
		}

		private void DeviceRemovedDebounced(object sender, EventArrivedEventArgs e)
		{
			if (_isClosing)
			{
				return;
			}
			removeDebounceTimer.Stop();
			removeDebounceTimer.Start();
		}

		private void RemoveDebounceTimerElapsed(object sender, ElapsedEventArgs e)
		{
			if (_isClosing)
			{
				return;
			}
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (_isClosing)
				{
					return;
				}

				bool otaBusy = IsOtaBusy();
				DisposeHidResources();

				if (!string.IsNullOrEmpty(_selectedDevicePath) && deviceStatus.ContainsKey(_selectedDevicePath) && deviceStatus[_selectedDevicePath])
				{
					deviceStatus[_selectedDevicePath] = false;
					deviceStatus.Remove(_selectedDevicePath);
				}

				_selectedDevicePath = null;
				_isDeviceReady = false;
				_otaHandshakeReady = false;
				StopBatchStartTimer();
				otaManager.Init();
				otaManager._needIdentification = true;
				if (IsAutoProductMode())
				{
					SetProductDisplayOverlay(null);
				}
				SetDeviceStatus("未连接", false);
				if (_prodTestMode)
				{
					if (_dutSentWaitingUnplug)
					{
						_dutSentWaitingUnplug = false;
						SetProdVisual("请插入下一台", "上一台已进入 DUT。USB 断开是正常现象。", false);
					}
					else
					{
						SetProdVisual("等待插入 Dongle", "插入后自动识别并进入 DUT", false);
					}
				}
				if (_batchUpgradeMode && !otaBusy)
				{
					if (_batchWaitReappear)
					{
						SetBatchVisual("等待重新连接", "数据已发送，等设备重启后再次读到才算升级成功", false);
					}
					else if (_batchAwaitUnplug)
					{
						ScheduleBatchAbsent();
						SetBatchVisual("请拔出", "拔出后出现「请插入下一台」，再插下一台", true);
					}
					else
					{
						SetBatchVisual("请插入下一台", "上一台已处理完，可继续插入", false);
					}
				}
				if (otaBusy)
				{
					otaManager.Release();
					LogMessage("升级中断：Dongle 已拔出");
					SetOtaFailed("升级过程中 Dongle 已断开，请重新连接后再试");
				}
				else
				{
					LogMessage("Dongle 已拔出");
					UpdateOtaButtonState();
				}
			}));
		}
		#endregion

		#region VID/PID文本变化事件
		/// <summary>
		/// VID输入框文本变化事件
		/// </summary>
		private void InfoVid_TextChanged(object sender, TextChangedEventArgs e)
		{
			TextBox textBox = sender as TextBox;
			string input = textBox?.Text?.Trim() ?? "";

			// 空输入时不报错，仅清除值
			if (string.IsNullOrEmpty(input))
			{
				VendorId = 0;
				return;
			}

			// 验证并转换十六进制值
			if (int.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int vendorId))
			{
				VendorId = vendorId;
				if (!_suppressVidPidLog)
				{
					SwitchProductComboToCustom();
					LogMessage($"VID已更新：0x{vendorId:X4}");
				}
			}
			else
			{
				TxtLog.AppendText($"无效的VID格式：{input}，仅支持十六进制（例如0642）\n");
			}
		}

		/// <summary>
		/// PID输入框文本变化事件
		/// </summary>
		private void InfoPid_TextChanged(object sender, TextChangedEventArgs e)
		{
			TextBox textBox = sender as TextBox;
			string input = textBox?.Text?.Trim() ?? "";

			// 空输入时不报错，仅清除值
			if (string.IsNullOrEmpty(input))
			{
				ProductId = 0;
				return;
			}

			// 验证并转换十六进制值
			if (int.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int productId))
			{
				ProductId = productId;
				if (!_suppressVidPidLog)
				{
					SwitchProductComboToCustom();
					LogMessage($"PID已更新：0x{productId:X4}");
				}
			}
			else
			{
				TxtLog.AppendText($"无效的PID格式：{input}，仅支持十六进制（例如0001）\n");
			}
		}
		#endregion

		#region 设备选择与连接（替换HidLibrary为新HID实现）
		// 查找设备按钮点击事件
		private void BtnFindDevice_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再查找设备");
				return;
			}
			if (_isFindingDevice)
			{
				return;
			}
			_isFindingDevice = true;
			try
			{
				FindAndConnectDongle();
			}
			finally
			{
				_isFindingDevice = false;
			}
		}

		private void FindAndConnectDongle(bool forceAuto = false, bool quiet = false)
		{
			string selectedTag = forceAuto
				? "AUTO"
				: ((ProductComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "AUTO");
			List<HidChoice> found = ScanDongles(selectedTag, quiet);
			if (_batchUpgradeMode && _batchAwaitUnplug)
			{
				return;
			}
			if (found.Count == 0)
			{
				if (string.Equals(selectedTag, "AUTO", StringComparison.OrdinalIgnoreCase))
				{
					SetProductDisplayOverlay(null);
				}
				SetDeviceStatus("未找到设备", false);
				if (!quiet)
				{
					LogMessage(selectedTag == "AUTO"
						? "未找到 Dongle"
						: string.Format("未找到目标设备  VID={0:X4} PID={1:X4}", VendorId, ProductId));
				}
				return;
			}

			if (found.Count == 1 || _batchUpgradeMode)
			{
				ApplyDetectedDongle(found[0]);
				_selectedDevicePath = found[0].Device.DevicePath;
				ConnectSelectedDeviceLikeOriginal();
				return;
			}

			DeviceComboBox.ItemsSource = found;
			DeviceComboBox.DisplayMemberPath = "Display";
			DeviceComboBox.SelectedIndex = 0;
			DeviceSelectionPopup.IsOpen = true;
			LogMessage(string.Format("找到 {0} 台 Dongle，请选择后点连接", found.Count));
		}

		private List<HidChoice> ScanDongles(string selectedTag, bool quiet = false)
		{
			if (string.Equals(selectedTag, "CUSTOM", StringComparison.OrdinalIgnoreCase))
			{
				return ScanCustomVidPid();
			}

			var result = new List<HidChoice>();
			if (!quiet)
			{
				LogMessage("正在自动识别 Dongle...");
			}

			foreach (KnownDongle dongle in KnownDongles)
			{
				List<HidDevice> devices = HidDevices.Enumerate(dongle.Vid, dongle.Pid, UsagePage).ToList();
				if (devices.Count == 0)
				{
					devices = HidDevices.Enumerate(dongle.Vid, dongle.Pid).ToList();
				}
				for (int i = 0; i < devices.Count; i++)
				{
					string suffix = devices.Count > 1 ? " #" + (i + 1) : "";
					result.Add(new HidChoice
					{
						Device = devices[i],
						ProductName = dongle.Name,
						VendorId = dongle.Vid,
						ProductId = dongle.Pid,
						Display = dongle.Name + suffix + "  " + dongle.VidHex + ":" + dongle.PidHex
					});
				}
			}
			return result;
		}

		private List<HidChoice> ScanCustomVidPid()
		{
			var result = new List<HidChoice>();
			string vidText = infoVid.Text?.Trim() ?? "";
			string pidText = infoPid.Text?.Trim() ?? "";
			if (!int.TryParse(vidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int vendorId) ||
				!int.TryParse(pidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int productId))
			{
				LogMessage("自定义 VID/PID 无效，请输入十六进制");
				return result;
			}

			VendorId = vendorId;
			ProductId = productId;
			LogMessage(string.Format("正在扫描自定义设备  VID={0:X4} PID={1:X4} ...", vendorId, productId));

			List<HidDevice> devices = HidDevices.Enumerate(vendorId, productId, UsagePage).ToList();
			if (devices.Count == 0)
			{
				devices = HidDevices.Enumerate(vendorId, productId).ToList();
			}
			for (int i = 0; i < devices.Count; i++)
			{
				string suffix = devices.Count > 1 ? " #" + (i + 1) : "";
				result.Add(new HidChoice
				{
					Device = devices[i],
					ProductName = "自定义",
					VendorId = vendorId,
					ProductId = productId,
					Display = "自定义" + suffix + "  " + vendorId.ToString("X4") + ":" + productId.ToString("X4")
				});
			}
			return result;
		}

		private void ApplyDetectedDongle(HidChoice choice)
		{
			if (choice == null)
			{
				return;
			}
			VendorId = choice.VendorId != 0 ? choice.VendorId : 0x38A6;
			ProductId = choice.ProductId;
			_pendingProductId = choice.ProductId;
			_suppressVidPidLog = true;
			infoVid.Text = VendorId.ToString("X4");
			infoPid.Text = choice.ProductId.ToString("X4");
			_suppressVidPidLog = false;
			if (string.Equals(choice.ProductName, "自定义", StringComparison.Ordinal))
			{
				SwitchProductComboToCustom();
			}
			else
			{
				SyncProductComboByPid(choice.ProductId.ToString("X4"));
			}
			LogMessage("识别到 " + choice.ProductName + "  VID=" + VendorId.ToString("X4") + " PID=" + choice.ProductId.ToString("X4"));
		}

		private void SwitchProductComboToCustom()
		{
			_suppressProductAutoConnect = true;
			for (int i = 0; i < ProductComboBox.Items.Count; i++)
			{
				if (ProductComboBox.Items[i] is ComboBoxItem item &&
					string.Equals(item.Tag as string, "CUSTOM", StringComparison.OrdinalIgnoreCase))
				{
					if (ProductComboBox.SelectedIndex != i)
					{
						ProductComboBox.SelectedIndex = i;
					}
					SetProductDisplayOverlay(null);
					SetVidPidEditable(true);
					SaveLastProductPid("CUSTOM");
					break;
				}
			}
			_suppressProductAutoConnect = false;
		}

		private void SyncProductComboByPid(string pidHex)
		{
			string productName = null;
			foreach (KnownDongle dongle in KnownDongles)
			{
				if (dongle.PidHex.Equals(pidHex, StringComparison.OrdinalIgnoreCase))
				{
					productName = dongle.Name;
					break;
				}
			}

			_suppressProductAutoConnect = true;
			for (int i = 0; i < ProductComboBox.Items.Count; i++)
			{
				if (ProductComboBox.Items[i] is ComboBoxItem item &&
					string.Equals(item.Tag as string, "AUTO", StringComparison.OrdinalIgnoreCase))
				{
					if (ProductComboBox.SelectedIndex != i)
					{
						ProductComboBox.SelectedIndex = i;
					}
					break;
				}
			}
			_suppressProductAutoConnect = false;
			SetVidPidEditable(false);
			SetProductDisplayOverlay(productName ?? pidHex);
			SaveLastProductPid("AUTO");
		}

		private void ConnectSelectedDeviceLikeOriginal()
		{
			if (string.IsNullOrEmpty(_selectedDevicePath))
			{
				return;
			}

			if (_hidFileStream != null)
			{
				DisposeHidResources();
			}

			try
			{
				_hidDeviceHandle = CreateFileW(
					lpFileName: _selectedDevicePath,
					dwDesiredAccess: GENERIC_READ | GENERIC_WRITE,
					dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE,
					lpSecurityAttributes: IntPtr.Zero,
					dwCreationDisposition: OPEN_EXISTING,
					dwFlagsAndAttributes: FILE_FLAG_OVERLAPPED,
					hTemplateFile: IntPtr.Zero
				);

				if (_hidDeviceHandle == (IntPtr)INVALID_HANDLE_VALUE)
				{
					int errorCode = Marshal.GetLastWin32Error();
					LogMessage($"创建设备句柄失败，错误码：{errorCode}（{new Win32Exception(errorCode).Message}）");
					SetDeviceStatus("连接失败", false);
					return;
				}

				_safeHidHandle = new SafeFileHandle(_hidDeviceHandle, ownsHandle: true);
				if (_safeHidHandle.IsInvalid || _safeHidHandle.IsClosed)
				{
					LogMessage("安全句柄创建失败，无法连接设备");
					SetDeviceStatus("连接失败", false);
					return;
				}

				_hidFileStream = new FileStream(
					handle: _safeHidHandle,
					access: FileAccess.ReadWrite,
					bufferSize: PacketSize,
					isAsync: true
				);

				StartAsyncRead();
				string connectedName = !string.IsNullOrEmpty(_detectedProductName)
					? _detectedProductName
					: string.Format("{0:X4}:{1:X4}", VendorId, ProductId);
				LogMessage("已连接 " + connectedName);
				SetDeviceStatus("已连接  " + connectedName, true);
				_isDeviceReady = true;
				_otaHandshakeReady = false;
				if (_prodTestMode)
				{
					otaManager.PrepareToUpdate();
					TryAutoEnterDut(connectedName);
				}
				else if (_batchUpgradeMode)
				{
					if (_batchWaitReappear)
					{
						_batchWaitReappear = false;
						_batchAwaitUnplug = true;
						StopBatchStartTimer();
						StopBatchAbsentTimer();
						if (BatchProgress != null) BatchProgress.Value = 100;
						if (BatchPercent != null) BatchPercent.Text = "100%";
						SetBatchVisual("升级成功", connectedName + "  已重新读到设备。请拔出后再插入下一台", true);
						LogMessage("批量升级成功：已重新读到 " + connectedName);
					}
					else if (_batchAwaitUnplug)
					{
						StopBatchStartTimer();
						SetBatchVisual("请先拔出", connectedName + "  本台已升级成功。请拔出后再插入下一台", true);
						LogMessage("批量升级：本台已升级，拔出前不会再次升级");
					}
					else
					{
						otaManager.PrepareToUpdate();
						SendFirmwareGuidIfReady();
						SetBatchVisual("即将升级", connectedName + "  正在进入升级", false);
						ScheduleBatchUpgrade();
					}
				}
				else
				{
					otaManager.PrepareToUpdate();
					SendFirmwareGuidIfReady();
				}
				UpdateOtaButtonState();
			}
			catch (Exception ex)
			{
				LogMessage("连接设备时出错: " + ex.Message);
				SetDeviceStatus("连接失败", false);
				DisposeHidResources();
			}
		}

		private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			// 先释放原有设备资源
			DisposeHidResources();
			otaManager.Init();
			UpdateOtaButtonState();

			// 记录选中设备路径
			if (DeviceComboBox.SelectedItem is HidChoice selected)
			{
				_selectedDevicePath = selected.Device.DevicePath;
				ApplyDetectedDongle(selected);
			}
			else if (DeviceComboBox.SelectedItem is HidDevice selectedDevice)
			{
				_selectedDevicePath = selectedDevice.DevicePath;
			}
		}

		private void DeviceSelectionPopup_OkButton_Click(object sender, RoutedEventArgs e)
		{
			ConnectSelectedDeviceLikeOriginal();
			DeviceSelectionPopup.IsOpen = false;
		}
		#endregion

		#region 文件读取（保留原有业务，适配新HID状态）
		private void BtnReadFile_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再选择文件");
				return;
			}
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Filter = "FOT文件 (*.fot)|*.fot";
			openFileDialog.FilterIndex = 1;
			openFileDialog.ValidateNames = true;
			openFileDialog.CheckFileExists = true;

			if (openFileDialog.ShowDialog() == true)
			{
				ApplyFirmwareFile(openFileDialog.FileName, sendGuid: true);
			}
		}

		private void ApplyFirmwareFile(string filePath, bool sendGuid)
		{
			try
			{
				byte[] otaData = File.ReadAllBytes(filePath);
				if (otaData.Length < 52)
				{
					LogMessage("固件文件无效或过小，请选择正确的 .fot 文件");
					_isFileReady = false;
					UpdateOtaButtonState();
					return;
				}
				otaManager.SetOtaData(otaData);
				if (_TextFilePath != null) _TextFilePath.Text = filePath;
				if (BatchFilePath != null) BatchFilePath.Text = filePath;
				LogMessage("成功读取文件：" + System.IO.Path.GetFileName(filePath));
				SaveLastFotPath(filePath);

				byte[] GUID = new byte[20];
				GUID[0] = 0x47;
				GUID[1] = 0x4D;
				GUID[2] = 0x0B;
				GUID[3] = 0x10;
				Array.Copy(otaData, 36, GUID, 4, 16);
				_firmwareGuidPayload = BitConverter.ToString(GUID).Replace("-", " ");
				if (sendGuid)
				{
					SendFirmwareGuidIfReady();
				}

				_isFileReady = true;
				UpdateOtaButtonState();
				if (_batchUpgradeMode && !_prodTestMode)
				{
					if (_isDeviceReady && _otaHandshakeReady)
					{
						TryAutoUpgrade();
					}
					else if (_isDeviceReady)
					{
						SetBatchVisual("即将升级", "正在进入升级", false);
						ScheduleBatchUpgrade();
					}
					else
					{
						SetBatchVisual("等待插入 Dongle", "已选择固件，插入后自动升级", false);
					}
				}
			}
			catch (Exception ex)
			{
				LogMessage("读取文件时出错：" + ex.Message);
				_isFileReady = false;
				UpdateOtaButtonState();
			}
		}

		private void TryRestoreLastFirmware()
		{
			string path = LoadLastFotPath();
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				ApplyFirmwareFile(path, sendGuid: false);
			}
		}
		#endregion

		#region OTA升级（保留原有业务逻辑）
		private void BtnSendData_Click(object sender, RoutedEventArgs e)
		{
			OtaStartSecondCheck();
			SetOtaProgress(0, "正在升级…");
			BtnSendData.IsEnabled = false;
			UpdateUiLock();
			otaManager.StartOTA();
			LogMessage("开始OTA升级...");
			UpdateOtaButtonState();
		}

		private void SendFirmwareGuidIfReady()
		{
			if (!_isDeviceReady || string.IsNullOrEmpty(_firmwareGuidPayload))
			{
				return;
			}
			LogMessage("设备已连接，发送固件 GUID");
			SendPayload(_firmwareGuidPayload);
		}

		private void OtaStartSecondCheck()
		{
			otaManager.SetNeedIdentification(false);
			otaManager.SendOtaIdentification();  //二次检验 主要用于校验耳机
			otaManager.sentIdentification = true;
			otaManager.Init();
		}
		#endregion

		#region 数据发送（替换HidReport为FileStream写入，保留原有业务解析）
		public void SendPayload(string payload)
		{
			const int MinParts = 4; // TAGT(2) + opcode(1) + length(1) 最少4部分
			const int MaxDataLength = PacketSize - 1; // 最大有效长度（63）

			// 分割指令并过滤空项（一次分割，避免重复处理）
			string[] hexParts = payload.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			int partCount = hexParts.Length;

			// 1. 基础校验：最少4部分 + 不超过最大长度（尽早拦截无效指令）
			if (partCount < MinParts || partCount > MaxDataLength)
			{
				MessageBox.Show(
					partCount < MinParts
						? $"指令不完整！至少需要{MinParts}部分（TAGT+opcode+length），当前{partCount}部分"
						: $"指令过长！最大支持{MaxDataLength}部分，当前{partCount}部分",
					"长度错误",
					MessageBoxButton.OK,
					MessageBoxImage.Error
				);
				return;
			}

			// 2. 解析并校验关键字段（合并格式校验与数据提取，减少循环）
			byte[] parsedData = new byte[partCount]; // 复用一个数组存储解析结果
			for (int i = 0; i < partCount; i++)
			{
				// 格式校验 + 数据解析（一次完成，避免二次循环）
				if (!byte.TryParse(hexParts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsedData[i]))
				{
					MessageBox.Show(
						$"第{i + 1}部分「{hexParts[i]}」格式错误！\n请使用空格分隔的十六进制（如\"47 4D 01 01 00\"）",
						"格式错误",
						MessageBoxButton.OK,
						MessageBoxImage.Error
					);
					return;
				}
			}

			// 3. 校验数据长度与length字段匹配（直接使用已解析的数组，避免重复解析）
			byte declaredLength = parsedData[3]; // length字段是第4部分（索引3）
			int actualDataLength = partCount - MinParts; // 实际数据长度 = 总部分 - 前4部分
			if (actualDataLength != declaredLength)
			{
				MessageBox.Show(
					$"数据长度不匹配！\nlength字段声明{declaredLength}字节，实际数据{actualDataLength}字节",
					"长度错误",
					MessageBoxButton.OK,
					MessageBoxImage.Error
				);
				return;
			}

			// 4. 组装数据包（复用解析结果数组，减少内存分配）
			byte[] packet = new byte[PacketSize];
			packet[0] = ReportId; // 固定ReportId
			Array.Copy(parsedData, 0, packet, 1, partCount); // 复制解析后的数据

			// 剩余位置补0（无需单独循环，未复制的部分默认就是0）
			SendPacket(packet);
		}

		public void SendPayload_OTA(byte[] payload)
		{
			int payloadLength = payload.Length;
			int offset = 0;
			const int HeaderSize = 5; // 头部总长度：1(特定位) + 3(固定标识) + 1(数据长度)

			while (offset < payloadLength)
			{
				int remainingLength = payloadLength - offset;
				// 数据部分最大长度 = 包总长度 - 头部长度
				int currentDataLength = Math.Min(remainingLength, PacketSize - HeaderSize);

				// 初始化64字节数据包
				byte[] packet = new byte[PacketSize];

				// 第一位：特定位
				packet[0] = ReportId;
				// 第二至四位：固定标识 0x47, 0x4D, 0x09
				packet[1] = 0x47;
				packet[2] = 0x4D;
				packet[3] = 0x09;
				// 第五位：数据长度
				packet[4] = (byte)currentDataLength;

				// 从第6位（索引5）开始填充真实数据
				Array.Copy(payload, offset, packet, HeaderSize, currentDataLength);

				// 剩余字节补0
				for (int i = HeaderSize + currentDataLength; i < PacketSize; i++)
				{
					packet[i] = 0x00;
				}

				// 发送数据包
				SendPacket(packet);

				// 移动偏移量
				offset += currentDataLength;
			}

			otaManager.RunDataSend();
		}

		private void SendPacket(byte[] packet)
		{
			if (_hidFileStream == null || !_hidFileStream.CanWrite)
			{
				LogMessage("设备未连接或不可写");
				return;
			}

			// 替换原HidReport写入为FileStream写入（新HID实现）
			try
			{
				_hidFileStream.Write(packet, 0, packet.Length);
				_hidFileStream.Flush();
				Console.WriteLine(BitConverter.ToString(packet));
			}
			catch (Exception ex)
			{
				Dispatcher.InvokeAsync(() => LogMessage($"发送失败：{ex.Message}"));
			}
		}
		#endregion

		#region 异步读取（新HID实现，替换原HidLibrary回调）
		private void StartAsyncRead()
		{
			if (_isClosing || _disposingHid || _hidFileStream == null || !_hidFileStream.CanRead || _isReading)
			{
				return;
			}

			_isReading = true;
			try
			{
				_hidFileStream.BeginRead(
					array: _readBuffer,
					offset: 0,
					numBytes: PacketSize,
					userCallback: OnFileStreamReadCompleted,
					stateObject: _hidFileStream
				);
			}
			catch
			{
				_isReading = false;
			}
		}

		private void OnFileStreamReadCompleted(IAsyncResult ar)
		{
			FileStream stream = ar.AsyncState as FileStream ?? _hidFileStream;
			try
			{
				if (_isClosing || _disposingHid || stream == null)
				{
					_isReading = false;
					try
					{
						stream?.EndRead(ar);
					}
					catch
					{
					}
					return;
				}

				int bytesRead = stream.EndRead(ar);
				_isReading = false;

				if (bytesRead > 0 && _readBuffer[0] == ReportId)
				{
					byte[] packetCopy = new byte[bytesRead];
					Array.Copy(_readBuffer, packetCopy, bytesRead);

					Dispatcher.InvokeAsync(() =>
					{
						if (_isClosing || packetCopy.Length < 5)
						{
							if (!_isClosing && packetCopy.Length < 5)
							{
								LogMessage("错误：数据包长度不足（至少需要4字节头部）");
							}
							return;
						}

						byte tag1 = packetCopy[1];
						byte tag2 = packetCopy[2];
						byte opcode = packetCopy[3];
						byte declaredLength = packetCopy[4];

						if (tag1 != 0x47 || tag2 != 0x4D)
						{
							LogMessage($"错误：头部标识无效（预期 0x47 0x4D，实际 0x{tag1:X2} 0x{tag2:X2}）");
							return;
						}

						int dataStartIndex = 5;
						int maxAvailableLength = packetCopy.Length - dataStartIndex;
						if (declaredLength > maxAvailableLength)
						{
							LogMessage($"错误：数据长度异常（声明 {declaredLength} 字节，实际剩余 {maxAvailableLength} 字节）");
							return;
						}

						byte[] validData = new byte[declaredLength];
						Array.Copy(packetCopy, dataStartIndex, validData, 0, declaredLength);

						switch (opcode)
						{
							case 0x87:
								TxtLog.AppendText("透传内容：" + ByteToHexString(validData) + Environment.NewLine);
								break;
							case 0x88:
								ProcessVersionInfo(validData);
								break;
							case 0x89:
								otaManager.ProcessData(validData);
								if (_batchUpgradeMode && !_batchAwaitUnplug && !_batchWaitReappear && !IsOtaBusy())
								{
									_otaHandshakeReady = true;
									TryAutoUpgrade();
								}
								break;
							case 0x8a:
								ProcessGUIDInfo(validData);
								break;
							case 0x8b:
								ProcessGUIDBack(validData);
								break;
							default:
								LogMessage($"警告：未知 opcode 0x{opcode:X2}");
								break;
						}
					});
				}

				if (!_isClosing && !_disposingHid)
				{
					StartAsyncRead();
				}
			}
			catch (Exception ex)
			{
				_isReading = false;
				if (_isClosing || _disposingHid)
				{
					return;
				}
				string msg = ex.Message ?? "";
				// 原版也会偶发“已取消”；不影响后续升级，不刷屏
				if (msg.IndexOf("取消", StringComparison.Ordinal) < 0
					&& msg.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) < 0)
				{
					LogMessage($"读取设备数据异常: {msg}");
				}
				if (!_isClosing && !_disposingHid && _hidFileStream != null)
				{
					StartAsyncRead();
				}
			}
		}
		#endregion

		#region 原有业务辅助方法（完整保留）
		private string ByteToHexString(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0)
				return "（空数据）";

			StringBuilder sb = new StringBuilder();
			foreach (byte b in bytes)
			{
				// 格式化为两位十六进制，不足两位补0（如 0x7 → "07"）
				sb.AppendFormat("{0:X2} ", b);
			}
			// 移除最后一个多余的空格
			return sb.ToString().TrimEnd();
		}

		private void ProcessVersionInfo(byte[] validData)
		{
			if (validData == null || validData.Length < 1)
			{
				LogMessage("错误：版本信息数据为空或长度不足（至少需要1字节设备标识）");
				return;
			}

			try
			{
				// 解析设备类型标识（第一个字节）
				byte deviceTypeCode = validData[0];
				string deviceTypeDesc;

				// 根据设备类型码映射具体描述（严格匹配01-04）
				switch (deviceTypeCode)
				{
					case 0x01:
						deviceTypeDesc = "TWS左耳版本号";
						break;
					case 0x02:
						deviceTypeDesc = "TWS右耳版本号";
						break;
					case 0x03:
						deviceTypeDesc = "立体耳机版本号";
						break;
					case 0x04:
						deviceTypeDesc = "Dongle版本号";
						break;
					default:
						deviceTypeDesc = $"未知设备类型（代码：0x{deviceTypeCode:X2}）";
						break;
				}

				// 处理版本数据（排除第一个设备类型字节）
				byte[] versionData = validData.Length > 1
					? validData.Skip(1).ToArray()
					: Array.Empty<byte>();

				// 版本数据转换为十六进制字符串（原始数据展示）
				string versionHex = versionData.Length > 0
					? BitConverter.ToString(versionData).Replace("-", " ")
					: "无版本数据";

				// 尝试转换为ASCII字符串（文本格式版本号）
				string versionAscii = "";
				try
				{
					// 去除字符串末尾的空字符，避免显示乱码
					versionAscii = Encoding.ASCII.GetString(versionData).TrimEnd('\0');
				}
				catch (Exception ex)
				{
					versionAscii = $"ASCII转换失败: {ex.Message}";
				}

				// 结构化日志输出
				TxtLog.AppendText($"=== 版本信息 ===\n");
				TxtLog.AppendText($"设备类型：{deviceTypeDesc}\n");
				TxtLog.AppendText($"版本原始数据（十六进制）：{versionHex}\n");
				TxtLog.AppendText($"版本文本信息（ASCII）：{versionAscii}\n");
			}
			catch (Exception ex)
			{
				LogMessage($"处理版本信息时出错：{ex.Message}");
			}
		}

		private void ProcessGUIDInfo(byte[] validData)
		{
			if (validData == null || validData.Length < 1)
			{
				LogMessage("错误：版本信息数据为空或长度不足（至少需要1字节设备标识）");
				return;
			}

			try
			{
				// 解析设备类型标识（第一个字节）
				byte deviceTypeCode = validData[0];
				string deviceTypeDesc;

				// 根据设备类型码映射具体描述（严格匹配01-04）
				switch (deviceTypeCode)
				{
					case 0x01:
						deviceTypeDesc = "TWS左耳GUID";
						break;
					case 0x02:
						deviceTypeDesc = "TWS右耳GUID";
						break;
					case 0x03:
						deviceTypeDesc = "立体耳机GUID";
						break;
					case 0x04:
						deviceTypeDesc = "DongleGUID";
						break;
					default:
						deviceTypeDesc = $"未知设备类型（代码：0x{deviceTypeCode:X2}）";
						break;
				}

				// 处理版本数据（排除第一个设备类型字节）
				byte[] versionData = validData.Length > 1
					? validData.Skip(1).ToArray()
					: Array.Empty<byte>();

				// 版本数据转换为十六进制字符串（原始数据展示）
				string versionHex = versionData.Length > 0
					? BitConverter.ToString(versionData).Replace("-", " ")
					: "无版本数据";

				// 结构化日志输出
				TxtLog.AppendText($"=== GUID信息 ===\n");
				TxtLog.AppendText($"设备类型：{deviceTypeDesc}\n");
				TxtLog.AppendText($"版本原始数据（十六进制）：{versionHex}\n");
			}
			catch (Exception ex)
			{
				LogMessage($"处理版本信息时出错：{ex.Message}");
			}
		}

		private void ProcessGUIDBack(byte[] validData)
		{
			if (validData[0] == 0)
			{
				const string tip = "固件和当前设备不匹配，请确认选对了对应机型的 .fot 文件后再试。";
				LogMessage(tip);
				MessageBox.Show(tip, "固件不匹配", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			else
			{
				otaManager.CheckIfReadyToUpdate();
			}
		}

		private void InitTitleVersion()
		{
			string version = GetAppVersion();
			if (TitleVersionText != null)
			{
				TitleVersionText.Text = "v" + version;
			}
		}

		private static string GetAppVersion()
		{
			Version version = Assembly.GetExecutingAssembly().GetName().Version;
			if (version == null)
			{
				return "1.0.1";
			}
			if (version.Revision <= 0)
			{
				return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", version.Major, version.Minor, version.Build);
			}
			return version.ToString();
		}

		public void OnReceiveVersion(int version)
		{
			int v1 = (version >> 12) & 0x0F;
			int v2 = (version >> 8) & 0x0F;
			int v3 = (version >> 4) & 0x0F;
			int v4 = version & 0x0F;
			string versionString = string.Format(CultureInfo.CurrentCulture, "版本号：{0}.{1}.{2}.{3}", v1, v2, v3, v4);
			Dispatcher.InvokeAsync(() => LogMessage($"固件版本号: {versionString}"));
		}

		public void LogMessage(string message)
		{
			if (_isClosing)
			{
				return;
			}
			string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + ShortenHidPaths(message);
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(() =>
				{
					if (!_isClosing)
					{
						AppendLog(line);
					}
				}), DispatcherPriority.Normal);
				return;
			}
			AppendLog(line);
		}

		private void AppendLog(string line)
		{
			TxtLog.AppendText(line + "\n");
			TxtLog.ScrollToEnd();
			if (ProdTestLog != null)
			{
				ProdTestLog.AppendText(line + "\n");
				ProdTestLog.ScrollToEnd();
			}
			if (BatchLog != null)
			{
				BatchLog.AppendText(line + "\n");
				BatchLog.ScrollToEnd();
			}
		}

		public void UpdateOtaButtonState()
		{
			bool busy = IsOtaBusy();
			BtnSendData.IsEnabled = _isDeviceReady && _isFileReady && !busy;
			UpdateUiLock();
			if (_TextOtaHint == null)
			{
				return;
			}
			if (busy)
			{
				_TextOtaHint.Text = "正在升级，请勿拔出设备";
			}
			else if (!_isDeviceReady)
			{
				_TextOtaHint.Text = "请先连接 Dongle";
			}
			else if (!_isFileReady)
			{
				_TextOtaHint.Text = "请先选择固件文件";
			}
			else
			{
				_TextOtaHint.Text = "可以开始升级";
			}
		}

		private bool IsOtaBusy()
		{
			return otaManager != null && otaManager.IsUpdating();
		}

		private void UpdateUiLock()
		{
			bool busy = IsOtaBusy();
			if (ProductComboBox != null) ProductComboBox.IsEnabled = !busy;
			if (BtnFindDevice != null) BtnFindDevice.IsEnabled = !busy;
			if (BtnReadFile != null) BtnReadFile.IsEnabled = !busy;
			if (BtnToggleCommand != null) BtnToggleCommand.IsEnabled = !busy;
			if (BtnUsbDevices != null) BtnUsbDevices.IsEnabled = !busy;
			if (CommandPanel != null) CommandPanel.IsEnabled = !busy;
		}

		private void HookPlaceholderCommandBoxes()
		{
			info05.TextChanged += (s, e) => RefreshPlaceholderCommandButtons();
			info06.TextChanged += (s, e) => RefreshPlaceholderCommandButtons();
			info07.TextChanged += (s, e) => RefreshPlaceholderCommandButtons();
			RefreshPlaceholderCommandButtons();
		}

		private void RefreshPlaceholderCommandButtons()
		{
			if (BtnCmd05 != null) BtnCmd05.IsEnabled = IsValidCommandPayload(info05.Text);
			if (BtnCmd06 != null) BtnCmd06.IsEnabled = IsValidCommandPayload(info06.Text);
			if (BtnCmd07 != null) BtnCmd07.IsEnabled = IsValidCommandPayload(info07.Text);
		}

		private static bool IsValidCommandPayload(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 4 || parts.Length > 63)
			{
				return false;
			}
			byte[] parsed = new byte[parts.Length];
			for (int i = 0; i < parts.Length; i++)
			{
				if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed[i]))
				{
					return false;
				}
			}
			return parsed[3] == parts.Length - 4;
		}

		private static readonly Regex HidPathRegex = new Regex(@"\\\\\?\\hid#[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex VidPidRegex = new Regex(@"vid_([0-9a-fA-F]+)&pid_([0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static string ShortenHidPaths(string message)
		{
			if (string.IsNullOrEmpty(message) || message.IndexOf("hid#", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return message;
			}
			return HidPathRegex.Replace(message, match =>
			{
				Match vidPid = VidPidRegex.Match(match.Value);
				return vidPid.Success
					? "HID " + vidPid.Groups[1].Value.ToUpperInvariant() + ":" + vidPid.Groups[2].Value.ToUpperInvariant()
					: "HID设备";
			});
		}

		public void SetOtaProgress(int progress, string status = null)
		{
			if (_isClosing)
			{
				return;
			}
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(() => SetOtaProgress(progress, status)));
				return;
			}
			int value = Math.Max(0, Math.Min(100, progress));
			ProgressBarOta.Value = value;
			if (_TextPercent != null)
			{
				_TextPercent.Text = value + "%";
			}
			if (_TextOtaStatus != null)
			{
				_TextOtaStatus.Text = string.IsNullOrEmpty(status)
					? (value >= 100 ? "升级完成" : "正在升级… " + value + "%")
					: status;
				_TextOtaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x77, 0x85));
			}
			if (BatchProgress != null)
			{
				BatchProgress.Value = value;
			}
			if (BatchPercent != null)
			{
				BatchPercent.Text = value + "%";
			}
			if (_batchUpgradeMode && value < 100)
			{
				SetBatchVisual("正在升级", string.IsNullOrEmpty(status) ? value + "%" : status, true);
			}
		}

		public void SetOtaFinished(string message)
		{
			if (_isClosing)
			{
				return;
			}
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(() => SetOtaFinished(message)));
				return;
			}
			ProgressBarOta.Value = 100;
			if (_TextPercent != null) _TextPercent.Text = "100%";
			if (_TextOtaStatus != null)
			{
				_TextOtaStatus.Text = message;
				_TextOtaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60));
			}
			UpdateOtaButtonState();
			if (!_batchUpgradeMode)
			{
				MessageBox.Show(message, "升级成功", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			else
			{
				_batchWaitReappear = true;
				_batchAwaitUnplug = false;
				StopBatchStartTimer();
				if (BatchProgress != null) BatchProgress.Value = 100;
				if (BatchPercent != null) BatchPercent.Text = "100%";
				SetBatchVisual("等待重新连接", "数据已发送。设备重启并再次读到后，才算升级成功", false);
				LogMessage("批量升级：数据已发送，等待设备重新连接");
			}
		}

		public void SetOtaFailed(string reason)
		{
			if (_isClosing)
			{
				return;
			}
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(() => SetOtaFailed(reason)));
				return;
			}
			if (_TextOtaStatus != null)
			{
				_TextOtaStatus.Text = "升级失败：" + reason;
				_TextOtaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
			}
			UpdateOtaButtonState();
			if (_batchUpgradeMode)
			{
				SetBatchVisual("升级失败", reason, false);
			}
			else
			{
				MessageBox.Show(reason, "升级失败", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		private static string PrefsPath
		{
			get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cm2206.prefs"); }
		}

		private int LoadLastProductIndex()
		{
			string pid = ReadPref("ProductPid");
			if (string.IsNullOrEmpty(pid))
			{
				return 0;
			}
			for (int i = 0; i < ProductComboBox.Items.Count; i++)
			{
				if (ProductComboBox.Items[i] is ComboBoxItem item && item.Tag as string == pid)
				{
					return i;
				}
			}
			return 0;
		}

		private void SaveLastProductPid(string pid)
		{
			WritePref("ProductPid", pid);
		}

		private string LoadLastFotPath()
		{
			return ReadPref("FotPath");
		}

		private void SaveLastFotPath(string path)
		{
			WritePref("FotPath", path);
		}

		private static string ReadPref(string key)
		{
			try
			{
				if (!File.Exists(PrefsPath))
				{
					return "";
				}
				foreach (string line in File.ReadAllLines(PrefsPath))
				{
					int eq = line.IndexOf('=');
					if (eq > 0 && line.Substring(0, eq) == key)
					{
						return line.Substring(eq + 1);
					}
				}
			}
			catch
			{
			}
			return "";
		}

		private static void WritePref(string key, string value)
		{
			try
			{
				Dictionary<string, string> map = new Dictionary<string, string>();
				if (File.Exists(PrefsPath))
				{
					foreach (string line in File.ReadAllLines(PrefsPath))
					{
						int eq = line.IndexOf('=');
						if (eq > 0)
						{
							map[line.Substring(0, eq)] = line.Substring(eq + 1);
						}
					}
				}
				map[key] = value ?? "";
				List<string> lines = new List<string>();
				foreach (var kv in map)
				{
					lines.Add(kv.Key + "=" + kv.Value);
				}
				File.WriteAllLines(PrefsPath, lines);
			}
			catch
			{
			}
		}

		private void SetDeviceStatus(string text, bool connected)
		{
			if (_TextDeviceStatus != null)
			{
				_TextDeviceStatus.Text = text;
			}
			if (StatusDot != null)
			{
				StatusDot.Fill = new SolidColorBrush(connected
					? Color.FromRgb(0x27, 0xAE, 0x60)
					: Color.FromRgb(0xC0, 0x39, 0x2B));
			}
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			WindowChrome.SetWindowChrome(this, new WindowChrome
			{
				CaptionHeight = 48,
				ResizeBorderThickness = new Thickness(6),
				GlassFrameThickness = new Thickness(0),
				CornerRadius = new CornerRadius(0),
				UseAeroCaptionButtons = false
			});
		}

		private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (e.ClickCount == 2)
			{
				CaptionMax_Click(sender, e);
				return;
			}
			if (e.LeftButton == MouseButtonState.Pressed)
			{
				DragMove();
			}
		}

		private void CaptionMinimize_Click(object sender, RoutedEventArgs e)
		{
			WindowState = WindowState.Minimized;
		}

		private void CaptionMax_Click(object sender, RoutedEventArgs e)
		{
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		}

		private void CaptionClose_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void Window_FileDragOver(object sender, DragEventArgs e)
		{
			e.Handled = true;
			e.Effects = CanAcceptFirmwareDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
		}

		private void Window_FileDrop(object sender, DragEventArgs e)
		{
			e.Handled = true;
			string path = GetDroppedFotPath(e);
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再选择文件");
				return;
			}
			ApplyFirmwareFile(path, sendGuid: true);
		}

		private static bool CanAcceptFirmwareDrop(DragEventArgs e)
		{
			return !string.IsNullOrEmpty(GetDroppedFotPath(e));
		}

		private static string GetDroppedFotPath(DragEventArgs e)
		{
			if (e?.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				return null;
			}
			string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null || files.Length == 0)
			{
				return null;
			}
			string path = files[0];
			return string.Equals(System.IO.Path.GetExtension(path), ".fot", StringComparison.OrdinalIgnoreCase)
				? path
				: null;
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			if (!_isClosing && IsOtaBusy())
			{
				MessageBoxResult result = MessageBox.Show(
					"正在升级，此时关闭可能导致设备无法启动。确定退出？",
					"确认退出",
					MessageBoxButton.YesNo,
					MessageBoxImage.Warning);
				if (result != MessageBoxResult.Yes)
				{
					e.Cancel = true;
					return;
				}
			}
			_isClosing = true;
			_disposingHid = true;
			DisposeHidResources();
			System.Threading.ThreadPool.QueueUserWorkItem(_ => StopDeviceWatchers());
			base.OnClosing(e);
		}

		private const double NormalWindowWidth = 700;
		private const double CommandWindowWidth = 1210;
		private bool _prodTestMode;
		private bool _batchUpgradeMode;
		private bool _batchAwaitUnplug;
		private bool _batchWaitReappear;
		private DateTime _batchCooldownUntilUtc = DateTime.MinValue;
		private bool _otaHandshakeReady;
		private bool _dutSentWaitingUnplug;
		private DateTime _dutCooldownUntilUtc = DateTime.MinValue;
		private DispatcherTimer _batchStartTimer;
		private DispatcherTimer _batchAbsentTimer;

		private void BtnBatchUpgrade_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再进入批量升级");
				return;
			}
			EnterBatchUpgrade();
		}

		private void BtnLeaveBatchUpgrade_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在升级，请等待完成后再返回");
				return;
			}
			LeaveBatchUpgrade();
		}

		private void EnterBatchUpgrade()
		{
			if (CommandPanel.Visibility == Visibility.Visible)
			{
				CommandPanel.Visibility = Visibility.Collapsed;
				Width = NormalWindowWidth;
				BtnToggleCommand.Content = "指令控制";
			}
			_prodTestMode = false;
			_batchUpgradeMode = true;
			UpgradePage.Visibility = Visibility.Collapsed;
			ProdTestPage.Visibility = Visibility.Collapsed;
			BatchUpgradePage.Visibility = Visibility.Visible;
			TitleAppText.Text = "CM2206 批量升级";
			if (BatchFilePath != null && _TextFilePath != null)
			{
				BatchFilePath.Text = _TextFilePath.Text;
			}
			LogMessage("进入批量升级：识别到 Dongle 后自动升级");
			if (!_isFileReady)
			{
				SetBatchVisual("请先选择固件", "选择固件后，插入 Dongle 会自动升级", false);
				return;
			}
			if (_isDeviceReady && _otaHandshakeReady && !_batchAwaitUnplug)
			{
				TryAutoUpgrade();
			}
			else if (_batchAwaitUnplug)
			{
				SetBatchVisual("请先拔出", "本台已升级。拔出后，再插入下一台才会升级", true);
			}
			else if (_isDeviceReady)
			{
				SetBatchVisual("即将升级", "正在进入升级", false);
				ScheduleBatchUpgrade();
			}
			else
			{
				SetBatchVisual("等待插入 Dongle", "已选择固件，插入后自动升级", false);
				FindAndConnectDongle(forceAuto: true);
			}
		}

		public void MarkBatchTransferDone()
		{
			if (_batchUpgradeMode)
			{
				_batchWaitReappear = true;
				_batchAwaitUnplug = false;
			}
		}

		private void LeaveBatchUpgrade()
		{
			_batchUpgradeMode = false;
			_batchAwaitUnplug = false;
			_batchWaitReappear = false;
			StopBatchStartTimer();
			StopBatchAbsentTimer();
			BatchUpgradePage.Visibility = Visibility.Collapsed;
			UpgradePage.Visibility = Visibility.Visible;
			TitleAppText.Text = "CM2206 固件升级";
			LogMessage("已返回升级页面");
		}

		private void ScheduleBatchAbsent()
		{
			if (_batchAbsentTimer == null)
			{
				_batchAbsentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
				_batchAbsentTimer.Tick += (s, e) =>
				{
					StopBatchAbsentTimer();
					if (!_batchUpgradeMode || !_batchAwaitUnplug)
					{
						return;
					}
					if (ScanDongles("AUTO", true).Count > 0)
					{
						return;
					}
					if (DateTime.UtcNow < _batchCooldownUntilUtc)
					{
						_batchAbsentTimer.Interval = _batchCooldownUntilUtc - DateTime.UtcNow;
						_batchAbsentTimer.Start();
						return;
					}
					_batchAwaitUnplug = false;
					SetBatchVisual("请插入下一台", "可以插入下一台 Dongle", false);
					LogMessage("批量升级：已确认拔出，可插入下一台");
				};
			}
			_batchAbsentTimer.Stop();
			_batchAbsentTimer.Start();
		}

		private void StopBatchAbsentTimer()
		{
			if (_batchAbsentTimer != null)
			{
				_batchAbsentTimer.Stop();
			}
		}

		private void ScheduleBatchUpgrade()
		{
			if (_batchStartTimer == null)
			{
				_batchStartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
				_batchStartTimer.Tick += (s, e) =>
				{
					StopBatchStartTimer();
					if (!_batchUpgradeMode || !_isDeviceReady || IsOtaBusy() || _batchAwaitUnplug || _batchWaitReappear)
					{
						return;
					}
					_otaHandshakeReady = true;
					TryAutoUpgrade();
				};
			}
			_batchStartTimer.Stop();
			_batchStartTimer.Start();
		}

		private void StopBatchStartTimer()
		{
			if (_batchStartTimer != null)
			{
				_batchStartTimer.Stop();
			}
		}

		private void SetBatchVisual(string status, string detail, bool active)
		{
			if (BatchStatusText != null)
			{
				BatchStatusText.Text = status;
			}
			if (BatchDetailText != null)
			{
				BatchDetailText.Text = detail;
			}
			if (BatchDot != null)
			{
				BatchDot.Fill = new SolidColorBrush(active
					? Color.FromRgb(0x27, 0xAE, 0x60)
					: Color.FromRgb(0xC0, 0x39, 0x2B));
			}
		}

		public void OnOtaHandshakeReady()
		{
			_otaHandshakeReady = true;
			if (_batchUpgradeMode && !_prodTestMode && !_batchWaitReappear && !_batchAwaitUnplug)
			{
				TryAutoUpgrade();
			}
		}

		private void TryAutoUpgrade()
		{
			if (!_batchUpgradeMode || _prodTestMode || !_isDeviceReady || !_otaHandshakeReady || IsOtaBusy() || _batchAwaitUnplug || _batchWaitReappear)
			{
				if (_batchAwaitUnplug && _batchUpgradeMode)
				{
					SetBatchVisual("请先拔出", "本台已升级。拔出后，再插入下一台才会升级", true);
				}
				return;
			}
			if (!_isFileReady)
			{
				SetBatchVisual("请先选择固件", "插入后不会升级", false);
				LogMessage("批量升级：尚未选择固件，插入后不会升级");
				return;
			}
			SetBatchVisual("正在升级", "请勿拔出 Dongle", true);
			StopBatchStartTimer();
			LogMessage("批量升级：开始自动升级");
			BtnSendData_Click(null, null);
		}

		private void BtnProdTest_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再进入产测");
				return;
			}
			EnterProdTest();
		}

		private void BtnLeaveProdTest_Click(object sender, RoutedEventArgs e)
		{
			LeaveProdTest();
		}

		private void EnterProdTest()
		{
			if (CommandPanel.Visibility == Visibility.Visible)
			{
				CommandPanel.Visibility = Visibility.Collapsed;
				Width = NormalWindowWidth;
				BtnToggleCommand.Content = "指令控制";
			}
			_prodTestMode = true;
			_batchUpgradeMode = false;
			UpgradePage.Visibility = Visibility.Collapsed;
			BatchUpgradePage.Visibility = Visibility.Collapsed;
			ProdTestPage.Visibility = Visibility.Visible;
			TitleAppText.Text = "CM2206 产测";
			LogMessage("进入产测模式：识别到 Dongle 后自动进入 DUT");
			if (_isDeviceReady)
			{
				string connectedName = !string.IsNullOrEmpty(_detectedProductName)
					? _detectedProductName
					: string.Format("{0:X4}:{1:X4}", VendorId, ProductId);
				TryAutoEnterDut(connectedName);
			}
			else
			{
				SetProdVisual("等待插入 Dongle", "插入后自动识别并进入 DUT", false);
				FindAndConnectDongle(forceAuto: true);
			}
		}

		private void LeaveProdTest()
		{
			_prodTestMode = false;
			_dutSentWaitingUnplug = false;
			ProdTestPage.Visibility = Visibility.Collapsed;
			UpgradePage.Visibility = Visibility.Visible;
			TitleAppText.Text = "CM2206 固件升级";
			LogMessage("已返回升级页面");
		}

		private void TryAutoEnterDut(string connectedName)
		{
			if (!_prodTestMode || !_isDeviceReady)
			{
				return;
			}
			if (otaManager != null && otaManager.IsUpdating())
			{
				return;
			}
			if (DateTime.UtcNow < _dutCooldownUntilUtc)
			{
				LogMessage("产测：刚发送过进入 DUT，本轮不再重复发送");
				return;
			}
			_dutCooldownUntilUtc = DateTime.UtcNow.AddSeconds(3);
			_dutSentWaitingUnplug = true;
			SetProdVisual("已进入 DUT", connectedName + "  已发送进入 DUT，USB 断开是正常现象", true);
			LogMessage("产测：已发送进入 DUT  " + connectedName);
			SendPayload(info0201.Text);
		}

		private void SetProdVisual(string status, string detail, bool active)
		{
			if (ProdTestStatusText != null)
			{
				ProdTestStatusText.Text = status;
			}
			if (ProdTestDetailText != null)
			{
				ProdTestDetailText.Text = detail;
			}
			if (ProdTestDot != null)
			{
				ProdTestDot.Fill = new SolidColorBrush(active
					? Color.FromRgb(0x27, 0xAE, 0x60)
					: Color.FromRgb(0xC0, 0x39, 0x2B));
			}
		}

		private void BtnToggleCommand_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作");
				return;
			}
			bool open = CommandPanel.Visibility != Visibility.Visible;
			CommandPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
			Width = open ? CommandWindowWidth : NormalWindowWidth;
			BtnToggleCommand.Content = open ? "关闭指令" : "指令控制";
		}

		private UsbDevicesWindow _usbDevicesWindow;

		private void BtnUsbDevices_Click(object sender, RoutedEventArgs e)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作");
				return;
			}
			if (_usbDevicesWindow != null)
			{
				_usbDevicesWindow.Activate();
				return;
			}
			_usbDevicesWindow = new UsbDevicesWindow(this);
			_usbDevicesWindow.Closed += (s, args) => _usbDevicesWindow = null;
			_usbDevicesWindow.Show();
		}

		public void ApplyCustomVidPid(string vidHex, string pidHex)
		{
			if (IsOtaBusy())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再切换产品");
				return;
			}
			_suppressVidPidLog = true;
			SwitchProductComboToCustom();
			infoVid.Text = vidHex;
			infoPid.Text = pidHex;
			_suppressVidPidLog = false;
			LogMessage(string.Format("已从 USB 列表填入  VID={0} PID={1}", vidHex, pidHex));
			BtnFindDevice_Click(null, null);
		}
		#endregion

		#region 各类测试按钮（完整保留原有业务）
		private void ButtonGoTest_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0101.Text);
		}

		private void ButtonOutTest_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0100.Text);
		}

		private void ButtonGoDut_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0201.Text);
		}

		private void ButtonOutDut_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0200.Text);
		}

		private void ButtonGoBtTest_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0c01.Text);
		}

		private void ButtonOutBtTest_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info0c00.Text);
		}

		private void ButtonDisConnect24G_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info03.Text);
		}

		private void ButtonFindConnectDevice_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info04.Text);
		}

		private void ButtonConnectOnlyDevice_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info05.Text);
		}

		private void ButtonUserDefined_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info06.Text);
		}

		private void ButtonTrans_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info07.Text);
		}

		private void ButtonGetVersion_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			string[] hexParts = info08.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (!byte.TryParse(hexParts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
			{
				throw new ArgumentException($"无效的十六进制格式：{hexParts[4]}，请使用空格分隔的十六进制字符串（如\"47 4D\"）");
			}
			version = value;

			if (value == 0x00 || value == 0xFF || value > 0x07)
			{
				LogMessage("无效的指令，检查数据位设定是否符合要求0x01-0x07之间均有回复。");
				return;
			}

			SendPayload(info08.Text);
		}

		private void ButtonGetGUID_Click(object sender, RoutedEventArgs e)
		{
			if (!_isDeviceReady)
			{
				LogMessage("错误：设备未连接，请先连接设备！");
				return;
			}
			if (otaManager.IsUpdating())
			{
				LogMessage("提示：正在进行 OTA 升级，请等待升级完成后再操作！");
				return;
			}

			SendPayload(info10.Text);
		}
		#endregion

		#region 资源释放（整合新HID资源，保留原有业务）
		private void CancelPendingHidIo()
		{
			try
			{
				IntPtr handle = IntPtr.Zero;
				if (_safeHidHandle != null && !_safeHidHandle.IsInvalid && !_safeHidHandle.IsClosed)
				{
					handle = _safeHidHandle.DangerousGetHandle();
				}
				else if (_hidDeviceHandle != IntPtr.Zero && _hidDeviceHandle != (IntPtr)INVALID_HANDLE_VALUE)
				{
					handle = _hidDeviceHandle;
				}
				if (handle != IntPtr.Zero)
				{
					CancelIoEx(handle, IntPtr.Zero);
				}
			}
			catch
			{
			}
		}

		private void StopDeviceWatchers()
		{
			try
			{
				if (insertWatcher != null)
				{
					insertWatcher.EventArrived -= DeviceInserted;
					insertWatcher.Stop();
					insertWatcher.Dispose();
					insertWatcher = null;
				}
			}
			catch
			{
			}
			try
			{
				if (removeWatcher != null)
				{
					removeWatcher.EventArrived -= DeviceRemovedDebounced;
					removeWatcher.Stop();
					removeWatcher.Dispose();
					removeWatcher = null;
				}
			}
			catch
			{
			}
			try
			{
				if (removeDebounceTimer != null)
				{
					removeDebounceTimer.Elapsed -= RemoveDebounceTimerElapsed;
					removeDebounceTimer.Stop();
					removeDebounceTimer.Dispose();
					removeDebounceTimer = null;
				}
			}
			catch
			{
			}
		}

		/// <summary>
		/// 安全释放HID相关资源（FileStream、SafeFileHandle、句柄）
		/// </summary>
		private void DisposeHidResources()
		{
			_disposingHid = true;
			_isReading = false;
			CancelPendingHidIo();

			FileStream stream = _hidFileStream;
			_hidFileStream = null;
			SafeFileHandle safeHandle = _safeHidHandle;
			_safeHidHandle = null;
			_hidDeviceHandle = IntPtr.Zero;

			System.Threading.ThreadPool.QueueUserWorkItem(_ => CloseHidStream(stream, safeHandle));

			if (!_isClosing)
			{
				_disposingHid = false;
			}

			if (otaManager != null)
			{
				otaManager._needIdentification = true;
			}
			_isDeviceReady = false;
		}

		private static void CloseHidStream(FileStream stream, SafeFileHandle safeHandle)
		{
			try
			{
				stream?.Close();
				stream?.Dispose();
			}
			catch
			{
			}

			// FileStream 会接管 SafeFileHandle；仅在流未创建成功时单独释放句柄
			if (stream == null)
			{
				try
				{
					if (safeHandle != null && !safeHandle.IsClosed)
					{
						safeHandle.Dispose();
					}
				}
				catch
				{
				}
			}
		}

		protected override void OnClosed(EventArgs e)
		{
			_selectedDevicePath = null;
			deviceStatus.Clear();
			loggedMessages.Clear();
			base.OnClosed(e);
		}
		#endregion
	}
}