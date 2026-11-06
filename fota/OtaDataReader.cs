using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public sealed class OtaDataReader : IDataReader
	{
		private const string TAG = "OtaDataReader";

		private readonly byte[] otaData;

		public OtaDataReader(byte[] otaData)
		{
			if (otaData == null)
			{
				throw new ArgumentNullException(nameof(otaData), "otaData cannot be null");
			}
			this.otaData = otaData;
		}

		public void Open()
		{
			// 这里假设 Logger 类有对应的 C# 实现，以下是模拟日志输出
			Console.WriteLine($"{TAG}: open");
		}

		public int GetSize()
		{
			Console.WriteLine($"{TAG}: getSize: {otaData.Length}");
			return otaData.Length;
		}

		public byte[] GetHash()
		{
			Console.WriteLine($"{TAG}: getHash");
			return GetHash(otaData);
		}

		public int Read(int srcPos, byte[] dst, int dstPos, int length)
		{
			Console.WriteLine($"{TAG}: read: srcPos={srcPos}, dstPos={dstPos}, length={length}");
			Array.Copy(otaData, srcPos, dst, dstPos, length);
			return length;
		}

		public void Close()
		{
			Console.WriteLine($"{TAG}: close");
		}

		private byte[] GetHash(byte[] data)
		{
			using (MD5 md5 = MD5.Create())
			{
				byte[] md5Hash = md5.ComputeHash(data);
				if (md5Hash != null)
				{
					byte[] hash = new byte[4];
					Array.Copy(md5Hash, 0, hash, 0, 4);
					return hash;
				}
				else
				{
					return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
				}
			}
		}
	}
}
