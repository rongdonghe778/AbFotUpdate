using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public interface IDataReader
	{
		/// <summary>
		/// 打开文件
		/// </summary>
		void Open();

		/// <summary>
		/// 获取文件的总大小
		/// </summary>
		/// <returns>文件的总大小</returns>
		int GetSize();

		/// <summary>
		/// 获取文件的 MD5 头 4 个字节
		/// </summary>
		/// <returns>文件的 MD5 头 4 个字节</returns>
		byte[] GetHash();

		/// <summary>
		/// 读取数据
		/// </summary>
		/// <param name="srcPos">源偏移量</param>
		/// <param name="dst">目标缓冲区</param>
		/// <param name="dstPos">目标缓冲区偏移量</param>
		/// <param name="length">读取大小</param>
		/// <returns>返回读取的大小，失败则为 -1</returns>
		int Read(int srcPos, byte[] dst, int dstPos, int length);

		/// <summary>
		/// 关闭文件
		/// </summary>
		void Close();
	}
}
