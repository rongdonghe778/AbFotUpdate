using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public class OtaError
	{
		// 蓝牙相关错误
		// REMOVED

		// 设备相关错误
		public static readonly OtaError REPORT_FROM_DEVICE = new OtaError(2000, "升级失败，请重试。若仍失败请联系我们。");
		public static readonly OtaError REFUSED_BY_DEVICE = new OtaError(2001, "设备拒绝了升级，请确认耳机已通过 2.4G 连接后再试。");
		public static readonly OtaError TIMEOUT_RECEIVE_RESPONSE = new OtaError(2002, "等待设备回复超时，请检查连接后重试。");

		// 数据文件相关错误
		public static readonly OtaError DATA_READER_ERROR = new OtaError(3000, "读取固件文件失败，请重新选择 .fot 文件。");

		private readonly int errorCode;
		private readonly string defaultDescription;
		private string userMessage;
		private byte deviceErrorCode; // 设备上报的错误代码，仅用于REPORT_FROM_DEVICE

		private OtaError(int errorCode, string description)
		{
			this.errorCode = errorCode;
			this.defaultDescription = description;
		}

		public int ErrorCode
		{
			get { return errorCode; }
		}

		public string Description
		{
			get { return string.IsNullOrEmpty(userMessage) ? defaultDescription : userMessage; }
		}

		public void SetUserMessage(string message)
		{
			userMessage = message;
		}

		public byte DeviceErrorCode
		{
			get { return deviceErrorCode; }
			set { deviceErrorCode = value; }
		}
	}
}
