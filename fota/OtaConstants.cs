using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AB.fota
{
	public static class OtaConstants
	{
		/// <summary>
		/// 默认的 MTU 大小（BLE 默认）
		/// </summary>
		public const int DEFAULT_MTU_SIZE = 23;

		/// <summary>
		/// 最大 MTU 大小
		/// </summary>
		public const int MAX_MTU_SIZE = 4 * 1024;

		/// <summary>
		/// 固件要求接收的每次分包的大小
		/// </summary>
		public const int DEFAULT_PACKET_SIZE = 240;

		/// <summary>
		/// 默认数据块大小（固件收到每一块后会回复确认）
		/// </summary>
		public const int DEFAULT_BLOCK_SIZE = 4 * 1024;

		/// <summary>
		/// 未定义块大小，取 -1（0xFFFFFFFF），至发送完毕都不需要固件确认数据接收
		/// </summary>
		public const uint UNDEFINED_BLOCK_SIZE = 0xFFFFFFFF;
	}
}
