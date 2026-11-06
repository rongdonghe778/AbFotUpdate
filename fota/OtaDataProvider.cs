using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public sealed class OtaDataProvider
	{
		private readonly IDataReader dataReader;

		private int dataSize;
		private bool isCompressedData = false;
		private int blockSize = unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE); // 不需要分包确认
		private int blockOffset = 0;
		private int packetSize = OtaConstants.DEFAULT_MTU_SIZE - 3;
		private int fileOffset = 0;

		public OtaDataProvider(IDataReader dataReader)
		{
			this.dataReader = dataReader;
		}

		public void Open()
		{
			dataReader.Open();
			dataSize = dataReader.GetSize();
			isCompressedData = CheckIfCompressedData();
		}

		public void Close()
		{
			fileOffset = 0;
			blockOffset = 0;
			packetSize = OtaConstants.DEFAULT_MTU_SIZE - 3;
			blockSize = unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE);
			dataReader.Close();
		}

		private int GetDataSize()
		{
			return dataSize;
		}

		public bool IsCompressedData()
		{
			return isCompressedData;
		}

		/// <summary>
		/// 获取开始升级的地址
		/// </summary>
		/// <returns>升级地址</returns>
		public int GetStartAddress()
		{
			return fileOffset;
		}

		/// <summary>
		/// 获取块偏移
		/// </summary>
		/// <returns>块偏移</returns>
		public int GetBlockOffset()
		{
			return blockOffset;
		}

		/// <summary>
		/// 设置开始升级的地址
		/// </summary>
		/// <param name="address">升级地址</param>
		public void SetStartAddress(int address)
		{
			fileOffset = address;
		}

		/// <summary>
		/// 设置块大小，固件接收一个块后会进行校验并做下一步处理，随后会回复状态到App。
		/// </summary>
		/// <param name="size">块大小</param>
		public void SetBlockSize(int size)
		{
			blockSize = size;
		}

		/// <summary>
		/// 设置发送一包的最大大小（被MTU大小限制）。
		/// </summary>
		/// <param name="packetSize">最大包大小</param>
		public void SetPacketSize(int packetSize)
		{
			this.packetSize = packetSize;
		}

		/// <summary>
		/// 获取升级进度
		/// </summary>
		/// <returns>升级进度</returns>
		public int GetProgress()
		{
			return fileOffset * 100 / GetDataSize();
		}

		/// <summary>
		/// 块是否已经完成发送
		/// </summary>
		/// <returns>阶段是否已完成发送</returns>
		public bool IsBlockSendFinish()
		{
			if (blockSize == unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE))
			{
				return IsAllDataSent();
			}
			else
			{
				return (fileOffset - blockOffset == blockSize) || IsAllDataSent();
			}
		}

		/// <summary>
		/// 是否已经发送完所有数据，即文件指针已到达文件末端。
		/// </summary>
		/// <returns>是否已经发送完所有数据</returns>
		public bool IsAllDataSent()
		{
			return fileOffset == GetDataSize();
		}

		/* Command Data */

		// 每一个块第一包必须严格按照GetStartAddress、GetTotalLengthToBeSent和
		// GetStartData的顺序使用，因为调用后会有一些值会被改变

		/// <summary>
		/// 返回需要升级的这个块一共要发送的数据长度
		/// </summary>
		/// <returns>需要发送的数据长度</returns>
		public int GetTotalLengthToBeSent()
		{
			// data length
			int sendLen;
			// 如果不需要分块发送
			if (blockSize == unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE))
			{
				sendLen = GetDataSize() - fileOffset;
			}
			// 如果需要分块发送
			else
			{
				// 剩下还没发送的长度
				int leftLen = GetDataSize() - fileOffset;
				sendLen = Math.Min(leftLen, blockSize);
				blockOffset = fileOffset;
			}

			return sendLen;
		}

		/// <summary>
		/// 返回需要升级的这个块头一包中固件数据
		/// </summary>
		/// <param name="headerSize">已经使用的包头长度，数据长度=包长度-包头长度</param>
		/// <returns>固件数据</returns>
		public byte[] GetStartData(int headerSize)
		{
			// 限定发送数据的长度
			int dataLen = (blockSize == unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE)) ? GetDataSize() : blockSize;  // 块长度
			dataLen = Math.Min(dataLen, packetSize - headerSize);                                        // 选择包长度还是包头长度
																										 // 判断整个文件有没有越界
			if (fileOffset + dataLen > GetDataSize())                                                  // 如果是属于最后一包，不够补一个dataLen长度了，就有多少放多少
			{
				dataLen = GetDataSize() - fileOffset;
			}

			byte[] readBuffer = new byte[dataLen];                                                       // 开个buf
																										 // 因为长度都是经过计算的，如果读取返回-1，那说明读取失败，抛出异常
			int readLength = dataReader.Read(fileOffset, readBuffer, 0, dataLen);
			if (readLength < 0)
			{
				throw new IOException("read data failed");
			}

			byte[] actualData = new byte[readLength];
			Array.Copy(readBuffer, 0, actualData, 0, readLength);
			fileOffset += readLength;

			return actualData;
		}

		/// <summary>
		/// 返回下一包中固件数据
		/// </summary>
		/// <param name="headerSize">已经使用的包头长度，数据长度=包长度-包头长度</param>
		/// <returns>固件数据</returns>
		public byte[] GetDataToBeSent(int headerSize)
		{
			// 限定发送数据的长度
			int dataLen = (blockSize == unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE)) ? GetDataSize() : blockSize;
			dataLen = Math.Min(dataLen, packetSize - headerSize);
			// 如果需要分块发送
			if (blockSize != unchecked((int)OtaConstants.UNDEFINED_BLOCK_SIZE))
			{
				// 判断是否超出blockSize
				if ((fileOffset - blockOffset) + dataLen > blockSize)
				{
					dataLen = blockSize - (fileOffset - blockOffset);
				}
			}
			// 判断整个文件有没有越界
			if (fileOffset + dataLen > GetDataSize())
			{
				dataLen = GetDataSize() - fileOffset;
			}

			byte[] readBuffer = new byte[dataLen];
			// 因为长度都是经过计算的，如果读取返回-1，那说明读取失败，抛出异常
			int readLength = dataReader.Read(fileOffset, readBuffer, 0, dataLen);
			if (readLength < 0)
			{
				throw new IOException("read data failed");
			}

			byte[] actualData = new byte[readLength];
			Array.Copy(readBuffer, 0, actualData, 0, readLength);
			fileOffset += readLength;

			return actualData;
		}

		/// <summary>
		/// 获取OTA文件的HASH值，供固件端用来对比文件。
		/// </summary>
		/// <returns>返回HASH值字节数组。</returns>
		public byte[] GetHash()
		{
			return dataReader.GetHash();
		}

		/* Private */

		private bool CheckIfCompressedData()
		{
			int HEADER_SIZE = 4;
			byte[] header = new byte[HEADER_SIZE];
			int readLen = dataReader.Read(0, header, 0, HEADER_SIZE);
			if (readLen == HEADER_SIZE)
			{
				return CheckHeaderIfCompressedData(header);
			}
			else
			{
				return false;
			}
		}

		private bool CheckHeaderIfCompressedData(byte[] data)
		{
			if (data.Length >= 3)
			{
				// POT
				return data[0] == 'P' && data[1] == 'O' && data[2] == 'T';
			}
			return false;
		}
	}

}
