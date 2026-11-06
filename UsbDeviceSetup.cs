using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AB
{
	internal static class UsbDeviceSetup
	{
		private const uint DIGCF_ALLCLASSES = 0x00000004;
		private const uint DIF_REMOVE = 0x00000005;
		private const uint DI_REMOVEDEVICE_GLOBAL = 0x00000001;
		private const uint SPDRP_DEVICEDESC = 0x00000000;
		private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
		private const uint DN_STARTED = 0x00000008;
		private const int CR_SUCCESS = 0;
		private static readonly IntPtr InvalidHandle = new IntPtr(-1);

		private static readonly Regex VidPidRegex = new Regex(
			@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		[StructLayout(LayoutKind.Sequential)]
		private struct SP_DEVINFO_DATA
		{
			public int cbSize;
			public Guid ClassGuid;
			public uint DevInst;
			public IntPtr Reserved;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SP_CLASSINSTALL_HEADER
		{
			public int cbSize;
			public uint InstallFunction;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SP_REMOVEDEVICE_PARAMS
		{
			public SP_CLASSINSTALL_HEADER ClassInstallHeader;
			public uint Scope;
			public uint HwProfile;
		}

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, IntPtr requiredSize);

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, IntPtr propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, IntPtr requiredSize);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetupDiOpenDeviceInfo(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_REMOVEDEVICE_PARAMS classInstallParams, int classInstallParamsSize);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiRemoveDevice(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

		[DllImport("cfgmgr32.dll")]
		private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

		public sealed class DeviceRecord
		{
			public string InstanceId;
			public string Name;
			public string Vid;
			public string Pid;
			public bool Connected;
		}

		public static List<DeviceRecord> EnumerateUsbRecords()
		{
			var list = new List<DeviceRecord>();
			AddEnumerator("USB", list);
			AddEnumerator("HID", list);
			return list;
		}

		public static string UninstallInstances(IList<string> instanceIds)
		{
			if (instanceIds == null || instanceIds.Count == 0)
			{
				return "没有可删除的设备记录";
			}

			List<string> ordered = new List<string>(instanceIds);
			ordered.Sort(CompareInstanceForRemove);

			int ok = 0;
			int fail = 0;
			string lastError = null;
			foreach (string instanceId in ordered)
			{
				if (string.IsNullOrEmpty(instanceId))
				{
					continue;
				}
				if (RemoveInstance(instanceId, out lastError))
				{
					ok++;
				}
				else
				{
					fail++;
				}
			}

			if (ok == 0)
			{
				return string.IsNullOrEmpty(lastError)
					? "删除失败"
					: "删除失败：" + lastError;
			}
			if (fail == 0)
			{
				return "已删除 " + ok + " 条 USB 设备记录";
			}
			return "已删除 " + ok + " 条，失败 " + fail + " 条" + (string.IsNullOrEmpty(lastError) ? "" : "（" + lastError + "）");
		}

		private static int CompareInstanceForRemove(string a, string b)
		{
			int scoreA = InstanceRemoveScore(a);
			int scoreB = InstanceRemoveScore(b);
			int cmp = scoreB.CompareTo(scoreA);
			return cmp != 0 ? cmp : string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
		}

		private static int InstanceRemoveScore(string instanceId)
		{
			if (string.IsNullOrEmpty(instanceId))
			{
				return 0;
			}
			int score = 0;
			if (instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
			{
				score += 20;
			}
			if (instanceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				score += 10;
			}
			return score;
		}

		private static void AddEnumerator(string enumerator, List<DeviceRecord> list)
		{
			IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_ALLCLASSES);
			if (set == IntPtr.Zero || set == InvalidHandle)
			{
				return;
			}
			try
			{
				uint index = 0;
				while (true)
				{
					SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
					info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
					if (!SetupDiEnumDeviceInfo(set, index, ref info))
					{
						break;
					}
					index++;

					StringBuilder idBuffer = new StringBuilder(1024);
					if (!SetupDiGetDeviceInstanceId(set, ref info, idBuffer, idBuffer.Capacity, IntPtr.Zero))
					{
						continue;
					}
					string instanceId = idBuffer.ToString();
					Match match = VidPidRegex.Match(instanceId);
					if (!match.Success)
					{
						continue;
					}

					uint status;
					uint problem;
					bool connected = CM_Get_DevNode_Status(out status, out problem, info.DevInst, 0) == CR_SUCCESS
						&& (status & DN_STARTED) != 0;

					list.Add(new DeviceRecord
					{
						InstanceId = instanceId,
						Name = GetDeviceName(set, ref info),
						Vid = match.Groups[1].Value.ToUpperInvariant(),
						Pid = match.Groups[2].Value.ToUpperInvariant(),
						Connected = connected
					});
				}
			}
			finally
			{
				SetupDiDestroyDeviceInfoList(set);
			}
		}

		private static string GetDeviceName(IntPtr set, ref SP_DEVINFO_DATA info)
		{
			string friendly = ReadProperty(set, ref info, SPDRP_FRIENDLYNAME);
			if (!string.IsNullOrWhiteSpace(friendly))
			{
				return friendly.Trim();
			}
			string desc = ReadProperty(set, ref info, SPDRP_DEVICEDESC);
			return string.IsNullOrWhiteSpace(desc) ? "" : desc.Trim();
		}

		private static string ReadProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
		{
			byte[] buffer = new byte[1024];
			if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, IntPtr.Zero, buffer, (uint)buffer.Length, IntPtr.Zero))
			{
				return "";
			}
			return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
		}

		private static bool RemoveInstance(string instanceId, out string error)
		{
			error = null;
			IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
			if (set == IntPtr.Zero || set == InvalidHandle)
			{
				error = "无法创建设备列表";
				return false;
			}
			try
			{
				SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
				info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
				if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
				{
					error = Win32Text();
					return false;
				}

				SP_REMOVEDEVICE_PARAMS remove = new SP_REMOVEDEVICE_PARAMS();
				remove.ClassInstallHeader.cbSize = Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
				remove.ClassInstallHeader.InstallFunction = DIF_REMOVE;
				remove.Scope = DI_REMOVEDEVICE_GLOBAL;
				remove.HwProfile = 0;

				SetupDiSetClassInstallParams(set, ref info, ref remove, Marshal.SizeOf(remove));
				if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref info) || SetupDiRemoveDevice(set, ref info))
				{
					return true;
				}
				error = Win32Text();
				return false;
			}
			finally
			{
				SetupDiDestroyDeviceInfoList(set);
			}
		}

		private static string Win32Text()
		{
			int code = Marshal.GetLastWin32Error();
			if (code == 5)
			{
				return "权限不足，请以管理员身份运行";
			}
			return "错误码 " + code;
		}
	}
}
