using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public sealed class OtaCommandGenerator
	{
		private const string TAG = nameof(OtaCommandGenerator);

		private static readonly byte[] FILE_SIZE = { 0x58, 0x00, 0x00, 0x00  }; //默认为固定值88 单位为Kb

		private static readonly byte[] CMD_OTA_IDENTIFICATION = { 0xCC, 0xAA, 0x55, 0xEE, 0x12, 0x19, 0xE4 };

		public const byte CMD_OTA_INFO = 0xA0;
		public const byte CMD_SEND_DATA = 0x20;
		public const byte CMD_GET_INFO = 0x91;
		public const byte CMD_NOTIFY_STATUS = 0x90;
		public const byte CMD_GET_INFO_TLV = 0x92;

		public const byte STATE_OK = 0x00; // 升级正常
		public const byte STATE_SAME_DEVICE = 0x01; // 要升级的固件和当前固件一致
		public const byte STATE_KEY = 0x02; // 芯片和程序之间的 KEY 不匹配
		public const byte STATE_FILE_FOT = 0x03;  // 远端升级文件格式错误，不是规定的 .fot 文件
		public const byte STATE_CRC_ERROR = 0x0B; // CRC 校验错误
		public const byte STATE_ERR_SEQ = 0x40;
		public const byte STATE_TWS_DISCONNECTED = 0x80;
		public const byte STATE_ERROR_STOP = 0x81;
		public const byte STATE_DONE = 0xFF;
		public const byte STATE_PAUSE = 0xFD;
		public const byte STATE_CONTINUE = 0xFE;

		public const byte CMD_GET_INFO_TYPE_VERSION = 0x01;
		public const byte CMD_GET_INFO_TYPE_UPDATE = 0x02;

		public const byte CMD_GET_INFO_TYPE_CAPABILITIES = 0x03;
		public const int INFO_CAPABILITIES_TWS = 0x0001;
		public const int INFO_CAPABILITIES_OTA = 0x0002;

		public const byte CMD_GET_INFO_TYPE_STATUS = 0x04;

		public const int INFO_STATUS_TWS_CONNECTED = 0x0001;
		public const byte CMD_GET_INFO_TYPE_CHANNEL = 0x06;
		public const byte INFO_CHANNEL_RIGHT = 0x00;
		public const byte INFO_CHANNEL_LEFT = 0x01;

		public const byte CMD_GET_INFO_TYPE_COMVERSION = 0x07;

		private byte seqNum = 0;
		private byte seqNumBlock = 0;

		/// <summary>
		/// 重置内部数据，之后可用于下一次升级。
		/// </summary>
		public void Reset()
		{
			Console.WriteLine($"{TAG}: Reset CommandGenerator");
			seqNum = 0;
		}

		/// <summary>
		/// 回退seq
		/// </summary>
		public void FallBack()
		{
			seqNum = seqNumBlock;
		}

		public OtaCommandGenerator()
		{
		}

		/* TLV format Command Data Generator */

		private byte[] GenerateCmdGetInfoData(byte cmd, byte[] value)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(cmd);
				ms.WriteByte((byte)value.Length);
				ms.Write(value, 0, value.Length);
				return ms.ToArray();
			}
		}


		private byte[] GenerateCmdGetInfoData(byte cmd)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(cmd);
				ms.WriteByte(0);
				return ms.ToArray();
			}
		}

		/* Info Command Data, used by TLV */

		/// <summary>
		/// 获取版本信息的命令（TLV格式）
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdDataGetInfoVersion()
		{
			return GenerateCmdGetInfoData(CMD_GET_INFO_TYPE_VERSION);
		}

		/// <summary>
		/// 获取升级信息的命令（TLV格式）
		/// </summary>
		/// <param name="version">当前固件版本</param>
		/// <param name="hashData">当前固件文件的HASH</param>
		/// <returns>命令数据</returns>
		public byte[] CmdDataGetInfoUpdate(int version, byte[] hashData)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				byte[] shortBytes = BitConverter.GetBytes((short)version);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(shortBytes, 0, 2);
				}
				else
				{
					for (int i = 1; i >= 0; i--)
					{
						ms.WriteByte(shortBytes[i]);
					}
				}

				if (hashData != null)
				{
					ms.Write(hashData, 0, 4);
				}
				else
				{
					byte[] intBytes = BitConverter.GetBytes(0xFFFFFFFF);
					if (BitConverter.IsLittleEndian)
					{
						ms.Write(intBytes, 0, 4);
					}
					else
					{
						for (int i = 3; i >= 0; i--)
						{
							ms.WriteByte(intBytes[i]);
						}
					}
				}

				return GenerateCmdGetInfoData(CMD_GET_INFO_TYPE_UPDATE, ms.ToArray());
			}
		}

		/// <summary>
		/// 获取TWS信息的命令（TLV格式）
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdDataGetInfoTWS()
		{
			return GenerateCmdGetInfoData(CMD_GET_INFO_TYPE_CAPABILITIES);
		}

		/// <summary>
		/// 获取状态的命令，TWS连接状态（TLV格式）
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdDataGetInfoStatus()
		{
			return GenerateCmdGetInfoData(CMD_GET_INFO_TYPE_STATUS);
		}

		/// <summary>
		/// 获取TWS通道信息的命令（TLV格式）
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdDataGetInfoChannel()
		{
			return GenerateCmdGetInfoData(CMD_GET_INFO_TYPE_CHANNEL);
		}

		/* Command with TLV format */

		/// <summary>
		/// 一次性获取全部信息
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdGetAllInfo()
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(CMD_GET_INFO_TLV);
				ms.WriteByte(seqNum++);

				try
				{
					ms.Write(CmdDataGetInfoVersion(), 0, CmdDataGetInfoVersion().Length);
					ms.Write(CmdDataGetInfoTWS(), 0, CmdDataGetInfoTWS().Length);
					ms.Write(CmdDataGetInfoStatus(), 0, CmdDataGetInfoStatus().Length);
					ms.Write(CmdDataGetInfoChannel(), 0, CmdDataGetInfoChannel().Length);
				}
				catch (Exception e)
				{
					// 这里假设 Logger 类有对应的 C# 实现，以下是模拟日志输出
					Console.WriteLine($"{TAG}: Failed to generate Command: {e.Message}");
					Console.WriteLine(e.StackTrace);
				}

				return ms.ToArray();
			}
		}

		/* Command */

		/// <summary>
		/// 供固件识别OTA的命令（固件接收到后用于准备OTA环境）
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdOtaIdentification()
		{
			return CMD_OTA_IDENTIFICATION;
		}

		/// <summary>
		/// 获取版本信息的命令
		/// </summary>
		/// <returns>命令数据</returns>
		public byte[] CmdGetInfoVersion()
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(CMD_GET_INFO);
				ms.WriteByte(seqNum++);
				ms.WriteByte(CMD_GET_INFO_TYPE_VERSION);
				return ms.ToArray();
			}
		}

		/// <summary>
		/// 获取升级信息的命令
		/// </summary>
		/// <param name="version">当前固件版本</param>
		/// <param name="hashData">当前固件文件的HASH</param>
		/// <returns>命令数据</returns>
		public byte[] CmdGetInfoUpdate(int version, byte[] hashData)
		{
			if (hashData == null)
			{
				throw new ArgumentNullException(nameof(hashData), "hashData cannot be null");
			}

			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(CMD_GET_INFO);
				ms.WriteByte(seqNum++);
				ms.WriteByte(CMD_GET_INFO_TYPE_UPDATE);
				byte[] shortBytes = BitConverter.GetBytes((short)version);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(shortBytes, 0, 2);
				}
				else
				{
					for (int i = 1; i >= 0; i--)
					{
						ms.WriteByte(shortBytes[i]);
					}
				}
				ms.Write(hashData, 0, 4);
				ms.Write(FILE_SIZE, 0, 4);
				return ms.ToArray();
			}
		}

		/* OTA Data header */

		public byte[] CmdStartSendHeader(int startAddress, int totalLengthToBeSent)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				seqNumBlock = seqNum;
				ms.WriteByte(CMD_OTA_INFO);
				ms.WriteByte(seqNum++);
				// 这里必须严格按照getStartAddress、getTotalLengthToBeSent和
				// getStartData的顺序使用，因为调用后会有一些值会被改变
				// start address
				byte[] startAddressBytes = BitConverter.GetBytes(startAddress);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(startAddressBytes, 0, 4);
				}
				else
				{
					for (int i = 3; i >= 0; i--)
					{
						ms.WriteByte(startAddressBytes[i]);
					}
				}
				// data length
				byte[] lengthBytes = BitConverter.GetBytes(totalLengthToBeSent);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(lengthBytes, 0, 4);
				}
				else
				{
					for (int i = 3; i >= 0; i--)
					{
						ms.WriteByte(lengthBytes[i]);
					}
				}
				return ms.ToArray();
			}
		}

		public byte[] CmdReSendHeader(int startAddress, int totalLengthToBeSent)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(CMD_OTA_INFO);
				ms.WriteByte(seqNum++);
				byte[] startAddressBytes = BitConverter.GetBytes(startAddress);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(startAddressBytes, 0, 4);
				}
				else
				{
					for (int i = 3; i >= 0; i--)
					{
						ms.WriteByte(startAddressBytes[i]);
					}
				}
				byte[] lengthBytes = BitConverter.GetBytes(totalLengthToBeSent);
				if (BitConverter.IsLittleEndian)
				{
					ms.Write(lengthBytes, 0, 4);
				}
				else
				{
					for (int i = 3; i >= 0; i--)
					{
						ms.WriteByte(lengthBytes[i]);
					}
				}
				return ms.ToArray();
			}
		}

		public byte[] CmdSendDataHeader()
		{
			using (MemoryStream ms = new MemoryStream())
			{
				ms.WriteByte(CMD_SEND_DATA);
				ms.WriteByte(seqNum++);
				return ms.ToArray();
			}
		}
	}
}
