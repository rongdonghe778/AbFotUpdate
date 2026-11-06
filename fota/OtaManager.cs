using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AB.fota
{
	public class OtaManager
	{
		private MainWindow mainWindow;

		private const string TAG = nameof(OtaManager);
		private const int DEFAULT_TIMEOUT = 10000; // in ms
		private const int UNDEFINED_FIRMWARE_VERSION = unchecked((int)0xFFFFFFFF);

		private readonly EventListener eventListener;
		private readonly Timer timeoutTimer;
		private readonly SynchronizationContext synchronizationContext;

		protected OtaDataProvider dataProvider;
		protected OtaCommandGenerator commandGenerator;
		protected int otaFirmwareVersion = UNDEFINED_FIRMWARE_VERSION;
		protected int deviceFirmwareVersion = UNDEFINED_FIRMWARE_VERSION;
		protected bool allowedUpdate;

		private int mBlockSize = OtaConstants.DEFAULT_BLOCK_SIZE;
		private int mPacketSize = OtaConstants.DEFAULT_PACKET_SIZE;

		protected bool isUpdating = false;
		protected bool isUpdatePause = false;

		// TWS升级时使用，释放时不需要重置
		protected bool? isTwsDevice = null; // 使用类是留给如果设备没有上传信息这种情况
		protected bool? isTwsConnected = null;
		protected bool? isAllowedOta = null;

		public bool disconnectedDueToDeviceError = false; // 报错后断开不再调用onStop

		// 识别
		public bool _needIdentification = true;
		public bool sentIdentification = false;
		protected static readonly int DELAY_AFTER_SEND_IDENTIFICATION = 200; // ms

		// 重发
		private bool isReSend = false;

		/* Constructor */
		/**
         * 构造器
         * @param eventListener 事件监听器{@link EventListener}
         */
		public OtaManager(EventListener eventListener,MainWindow mainWindow)
		{
			this.eventListener = eventListener;
			this.mainWindow = mainWindow;
			this.synchronizationContext = SynchronizationContext.Current;
			this.timeoutTimer = new Timer(HandleTimeout);
			this.commandGenerator = new OtaCommandGenerator();
			// 在构造函数中初始化 SynchronizationContext
			synchronizationContext = System.Threading.SynchronizationContext.Current;
			if (synchronizationContext == null)
			{
				// 如果当前上下文为空，可以创建一个新的上下文
				synchronizationContext = new System.Threading.SynchronizationContext();
			}
		}

		/* Public */

		/**
         * OTA Manager初始化
         */
		public void Init()
		{
			commandGenerator.Reset();
		}

		public void PrepareNewConnection()
		{
			CancelTimeout();
			isUpdating = false;
			isUpdatePause = false;
			sentIdentification = false;
			isReSend = false;
			allowedUpdate = false;
			_needIdentification = true;
			commandGenerator.Reset();
		}

		/**
         * 释放资源，包括断开蓝牙BLE和SPP连接。
         * 通常在升级完成、遇到错误，或者超时的时候，会自动释放资源，但是如果需要中断升级，亦可手动调用。
         */
		public void Release()
		{
			lock (this)
			{
				otaFirmwareVersion = UNDEFINED_FIRMWARE_VERSION;
				deviceFirmwareVersion = UNDEFINED_FIRMWARE_VERSION;
				allowedUpdate = false;
				isUpdating = false;
				isUpdatePause = false;
				sentIdentification = false;
				isReSend = false;
				_needIdentification = true;
				CancelTimeout();
				commandGenerator.Reset();

				if (dataProvider != null)
				{
					try
					{
						dataProvider.Close();
					}
					catch (IOException e)
					{
						// 这里异常不影响结果
						Console.WriteLine(e.StackTrace);
					}
				}
			}
		}

		public bool IsCompressedData()
		{
			return dataProvider.IsCompressedData();
		}

		protected int GetPacketSize()
		{
			return mPacketSize;
		}

		protected int GetBlockSize()
		{
			return mBlockSize;
		}

		public void PrepareToUpdate()
		{
			if (NeedIdentification())
			{
				// 发送OTA识别信息
				SendOtaIdentification();
				this._needIdentification = false;
			}
			else
			{
				// 获取设备信息
				GetAllInfo();
			}
		}

		/**
         * 开始进行OTA升级，升级之前必须先判断{@link #isReadyToUpdate()}。
         */
		public void StartOTA()
		{
			isUpdating = true;
			NotifyOnStart();
			// 从获取版本号开始吧
			GetOtaInfoVersion();
			mainWindow.Dispatcher.InvokeAsync(() =>
			{
				mainWindow.BtnSendData.IsEnabled = false;
			});
		}

		/**
         * 判断设备端是否已经就绪
         * @return 是否已就绪
         */
		public bool IsDeviceReady()
		{
			if (mainWindow._hidFileStream == null || !mainWindow._hidFileStream.CanWrite)
			{
				return false;
			}
			return true;

			/*Console.WriteLine($"{TAG}: isDeviceReady: isTwsDevice = {isTwsDevice}, isTwsConnected = {isTwsConnected}");

			// 一定需要getAllInfo有回复之后才能继续判断
			if (isTwsDevice == null)
			{
				return false;
			}
			else
			{
				if (isTwsDevice.Value)
				{
					return IsTwsConnected();
				}
				else
				{
					return true;
				}
			}*/
		}

		/**
         * 判断条件（设备和数据）是否已经全部就绪。
         * @return 是否已就绪
         */
		public bool IsReadyToUpdate()
		{
			Console.WriteLine($"{TAG}: isReadyToUpdate: DeviceReady = {IsDeviceReady()}, DataReady = {dataProvider != null}");
			return IsDeviceReady() && dataProvider != null;
		}

		protected bool CanSendNow()
		{
			return allowedUpdate && !dataProvider.IsBlockSendFinish();
		}

		/**
         * 设置OTA文件数据
         * @param otaData OTA数据
         */
		public void SetOtaData(byte[] otaData)
		{
			if (otaData == null)
			{
				throw new ArgumentNullException(nameof(otaData), "otaData cannot be null");
			}
			OtaDataReader dataReader = new OtaDataReader(otaData);
			SetDataReader(dataReader);
		}

		public void SetDataReader(IDataReader dataReader)
		{
			if (dataReader == null)
			{
				throw new ArgumentNullException(nameof(dataReader), "dataReader cannot be null");
			}
			try
			{
				dataProvider = new OtaDataProvider(dataReader);
				dataProvider.Open();
				dataProvider.SetBlockSize(GetBlockSize());                  //块大小 4096
				dataProvider.SetPacketSize(GetPacketSize());                //包大小 240
				//CheckIfReadyToUpdate();
			}
			catch (Exception e)
			{
				Console.WriteLine(e.StackTrace);
				NotifyOnError(OtaError.DATA_READER_ERROR);
			}
		}

		/**
         * 是否正在进行升级
         * @return 是否正在升级
         */
		public bool IsUpdating()
		{
			return isUpdating;
		}

		/**
         * 当前设备是否是TWS设备
         * @return 是否TWS设备
         */
		public bool IsTwsDevice()
		{
			return isTwsDevice.HasValue && isTwsDevice.Value;
		}

		/**
         * TWS对耳是否已连接
         * @return TWS是否已连接
         */
		public bool IsTwsConnected()
		{
			return isTwsConnected.HasValue && isTwsConnected.Value;
		}

		// 通知

		/**
         * 如果准备好了，就发出通知
         */
		public void CheckIfReadyToUpdate()
		{
			bool isReadyToUpdate = IsReadyToUpdate();
			Console.WriteLine($"{TAG}: checkIfReadyToUpdate: {isReadyToUpdate}");
			mainWindow.Dispatcher.InvokeAsync(() =>
			{
				mainWindow.OnOtaHandshakeReady();
				if (isReadyToUpdate)
				{
					mainWindow.BtnSendData.IsEnabled = true;
				}
			});
			if (isReadyToUpdate)
			{
				NotifyOnReady();
			}
		}

		// 以下Notify方法主要用于界面交互 尚未真实实现作用！！！

		protected void NotifyOnReady()
		{
			synchronizationContext.Post(_ => eventListener.OnOtaReady(), null);
		}

		protected void NotifyOnStart()
		{
			mainWindow.Dispatcher.InvokeAsync(() => mainWindow.LogMessage("OTA进行中 请等待"));
			synchronizationContext.Post(_ => eventListener.OnOtaStart(), null);
		}

		protected void NotifyOnProgress(int progress)
		{
			mainWindow.SetOtaProgress(progress);
			synchronizationContext.Post(_ => eventListener.OnOtaProgress(progress), null);
		}

		protected void NotifyOnStop()
		{
			if (!disconnectedDueToDeviceError)
			{
				synchronizationContext.Post(_ => eventListener.OnOtaStop(), null);
			}
			disconnectedDueToDeviceError = false;
		}

		protected void NotifyOnFinish()
		{
			//mainWindow._lastSelectDevice = null;
			mainWindow.loggedMessages.Clear();
			mainWindow._isDeviceReady = false;
			mainWindow.MarkBatchTransferDone();
			mainWindow.UpdateOtaButtonState();
			mainWindow.Dispatcher.InvokeAsync(() =>
			{
				mainWindow.LogMessage("OTA已全部完成 等待设备重启");
				mainWindow.SetOtaFinished("OTA 升级完成，请等待设备重启");
			});
			synchronizationContext.Post(_ => eventListener.OnOtaFinish(), null);
			mainWindow._hidFileStream = null;
		}

		protected void NotifyOnPause()
		{
			synchronizationContext.Post(_ => eventListener.OnOtaPause(), null);
		}

		protected void NotifyOnContinue()
		{
			synchronizationContext.Post(_ => eventListener.OnOtaContinue(), null);
		}

		protected void NotifyOnError(OtaError error)
		{
			mainWindow.loggedMessages.Clear();
			disconnectedDueToDeviceError = true;
			synchronizationContext.Post(_ => eventListener.OnOtaError(error), null);
			string message = error.Description;
			error.SetUserMessage(null);
			mainWindow.SetOtaFailed(message);
			Release();
		}

		protected void NotifyOnErrorStop()
		{
			//新增内容
			//mainWindow._lastSelectDevice = null;
			mainWindow.loggedMessages.Clear();
			mainWindow.ProgressBarOta.Dispatcher.InvokeAsync(() =>
			{
				mainWindow.ProgressBarOta.Value = 0;
				mainWindow.ProgressBarOta.Visibility = Visibility.Visible;
			});
			mainWindow.Dispatcher.InvokeAsync(() =>
			{
				mainWindow.BtnSendData.IsEnabled = false;
				mainWindow.LogMessage("耳机未连接，请先连接耳机并重新查找设备");
				mainWindow.UpdateOtaButtonState();
			});
		}

		protected void NotifyTWSDisconnected()
		{
			synchronizationContext.Post(_ => eventListener.OnTWSDisconnected(), null);
		}

		protected void NotifyOnReceiveVersion(int version)
		{
			mainWindow.OnReceiveVersion(version);
			synchronizationContext.Post(_ => eventListener.OnReceiveVersion(version), null);
		}

		protected void NotifyOnReceiveIsTWS(bool isTwsDevice)
		{
			synchronizationContext.Post(_ => eventListener.OnReceiveIsTWS(isTwsDevice), null);
		}

		protected void NotifyOnReceiveTWSConnected(bool connected)
		{
			synchronizationContext.Post(_ => eventListener.OnReceiveTWSConnected(connected), null);
		}

		protected void NotifyOnReceiveChannel(bool isLeftChannel)
		{
			synchronizationContext.Post(_ => eventListener.OnReceiveChannel(isLeftChannel), null);
		}

		protected void NotifyOnReceiveComVersion(string comVersion)
		{
			synchronizationContext.Post(_ => eventListener.OnReceiveComVersion(comVersion), null);
		}

		// 发送的命令

		protected void GetOtaInfoVersion()
		{
			byte[] cmd = commandGenerator.CmdGetInfoVersion();
			BtSendData(cmd);
			// 等待固件回复
			WaitingForTimeout();
		}

		protected void GetOtaInfoUpdate(int version)
		{
			if (dataProvider != null)
			{
				try
				{
					byte[] hash = dataProvider.GetHash();
					byte[] cmd = commandGenerator.CmdGetInfoUpdate(version, hash);
					BtSendData(cmd);
					// 等待固件回复
					WaitingForTimeout();
				}
				catch (IOException e)
				{
					Console.WriteLine(e.StackTrace);
					NotifyOnError(OtaError.DATA_READER_ERROR);
				}
			}
		}

		public void SendOtaIdentification()
		{
			byte[] cmd = commandGenerator.CmdOtaIdentification();
			BtSendData(cmd);
		}

		protected void GetAllInfo()
		{
			byte[] cmd = commandGenerator.CmdGetAllInfo();
			BtSendData(cmd);
			// 等待固件回复
			WaitingForTimeout();
		}

		protected void SendOtaStart()
		{
			int startAddress = dataProvider.GetStartAddress();
			int totalLengthToBeSent = dataProvider.GetTotalLengthToBeSent();
			byte[] header = commandGenerator.CmdStartSendHeader(startAddress, totalLengthToBeSent);
			int headerSize = header.Length;
			isReSend = false;

			try
			{
				byte[] data = dataProvider.GetStartData(headerSize);

				byte[] cmd = new byte[headerSize + data.Length];
				Array.Copy(header, 0, cmd, 0, headerSize);
				Array.Copy(data, 0, cmd, headerSize, data.Length);

				BtSendData(cmd);
			}
			catch (IOException e)
			{
				Console.WriteLine(e.StackTrace);
				NotifyOnError(OtaError.DATA_READER_ERROR);
			}

			// 进度
			int progress = dataProvider.GetProgress();
			NotifyOnProgress(progress);
		}

		protected void ReSendOtaStart()
		{
			if (!isReSend)
			{
				isReSend = true;
				int blockOffset = dataProvider.GetBlockOffset();    //回退位置到当前块
				dataProvider.SetStartAddress(blockOffset);
				commandGenerator.FallBack();                        //回退seq

				int startAddress = dataProvider.GetStartAddress();
				int totalLengthToBeSent = dataProvider.GetTotalLengthToBeSent();
				byte[] header = commandGenerator.CmdReSendHeader(startAddress, totalLengthToBeSent);
				int headerSize = header.Length;

				try
				{
					byte[] data = dataProvider.GetStartData(headerSize);

					byte[] cmd = new byte[headerSize + data.Length];
					Array.Copy(header, 0, cmd, 0, headerSize);
					Array.Copy(data, 0, cmd, headerSize, data.Length);
					BtSendData(cmd);
				}
				catch (IOException e)
				{
					Console.WriteLine(e.StackTrace);
					NotifyOnError(OtaError.DATA_READER_ERROR);
				}
			}

			// 进度
			int progress = dataProvider.GetProgress();
			NotifyOnProgress(progress);
		}

		protected void SendOtaData()
		{
			byte[] header = commandGenerator.CmdSendDataHeader();
			int headerSize = header.Length;

			try
			{
				byte[] data = dataProvider.GetDataToBeSent(headerSize);

				byte[] cmd = new byte[headerSize + data.Length];
				Array.Copy(header, 0, cmd, 0, headerSize);
				Array.Copy(data, 0, cmd, headerSize, data.Length);

				BtSendData(cmd);
			}
			catch (IOException e)
			{
				Console.WriteLine(e.StackTrace);
				NotifyOnError(OtaError.DATA_READER_ERROR);
			}

			// 进度
			int progress = dataProvider.GetProgress();
			NotifyOnProgress(progress);
		}

		// 发送相关

		protected void SendOtaDataOnce()
		{
			SendOtaData();
		}

		public void RunDataSend()
		{
			// 如果不需要发送第一串识别码，或者已经发过识别码，则进入发送流程
			if (!NeedIdentification() || sentIdentification)
			{
				// 如果可以，就一直发
				if (CanSendNow())
				{
					SendOtaDataOnce();
				}
				// 发完块的最后一包后，就等待固件回复
				else if (dataProvider != null && dataProvider.IsBlockSendFinish())
				{
					// 等待固件回复
					WaitingForTimeout();
				}
			}
			else
			{
				// 延迟发送，等待固件准备环境
				Task.Run(async () =>
				{
					// 延迟指定时间
					await Task.Delay(DELAY_AFTER_SEND_IDENTIFICATION);

					sentIdentification = true;
					// 获取设备信息
					GetAllInfo();
				});

				//new Timer(_ =>
				//{
				//	sentIdentification = true;
				//	// 获取设备信息
				//	GetAllInfo();
				//}, null, DELAY_AFTER_SEND_IDENTIFICATION, Timeout.Infinite);
			}
		}

		private void WaitingForTimeout()
		{
			timeoutTimer.Change(DEFAULT_TIMEOUT, Timeout.Infinite);
		}

		private void HandleTimeout(object state)
		{
			// 有时候出现一种极限情况，如果判断可以发送，但是还没发送前
			// 来了暂停，还是会发送然后等超时，所以在这加一个发送后判断
			if (isUpdatePause) return;

			Console.WriteLine($"{TAG}: Timed out while waiting for response: {isUpdating}");

			lock (this)
			{
				if (isUpdating)
				{
					// 错误通知
					NotifyOnError(OtaError.TIMEOUT_RECEIVE_RESPONSE);
				}
			}
		}

		private void CancelTimeout()
		{
			timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
		}

		// 接收数据处理

		public bool ProcessData(byte[] data)
		{
			Console.WriteLine(BitConverter.ToString(data));
			if (data.Length < 3)
			{
				Console.WriteLine($"{TAG}: The length of received data less than 3");
				return false;
			}

			using (MemoryStream ms = new MemoryStream(data))
			using (BinaryReader br = new BinaryReader(ms))
			{
				byte cmdType = br.ReadByte();
				byte seqNum = br.ReadByte(); // 暂时没使用

				switch (cmdType)
				{
					case OtaCommandGenerator.CMD_NOTIFY_STATUS:
						byte status = br.ReadByte();
						ProcessNotifyStatus(status);
						return true;
					case OtaCommandGenerator.CMD_GET_INFO:
						CancelTimeout();
						byte cmdSubType = br.ReadByte();
						byte[] extraData = br.ReadBytes((int)(ms.Length - ms.Position));
						ProcessGetInfo(cmdSubType, extraData);
						return true;
					case OtaCommandGenerator.CMD_GET_INFO_TLV:
						CancelTimeout();
						byte[] infoData = br.ReadBytes((int)(ms.Length - ms.Position));
						ProcessGetInfoTLV(infoData);
						return true;
				}
			}

			return false;
		}

		protected void ProcessNotifyStatus(byte status)
		{
			switch (status)
			{
				case OtaCommandGenerator.STATE_OK:
					CancelTimeout();     //取消超时
										 // 如果没有暂停，且还没发完，继续发；发完了就等STATE_DONE
					if (!isUpdatePause && !dataProvider.IsAllDataSent())
					{
						SendOtaStart();
					}
					break;
				case OtaCommandGenerator.STATE_SAME_DEVICE:
					FailFromDevice(status, "当前固件已是这个版本，无需重复升级。");
					break;
				case OtaCommandGenerator.STATE_KEY:
					FailFromDevice(status, "固件和当前设备不匹配，请确认选对了对应机型的 .fot 文件。");
					break;
				case OtaCommandGenerator.STATE_FILE_FOT:
					FailFromDevice(status, "固件文件格式不正确，请选择规定的 .fot 文件。");
					break;
				case OtaCommandGenerator.STATE_CRC_ERROR:
					FailFromDevice(status, "固件校验失败，请重新选择文件后再试。若仍失败请联系我们。");
					break;
				case OtaCommandGenerator.STATE_DONE:
					CancelTimeout();
					// DONE
					NotifyOnFinish();
					Release();
					break;
				case OtaCommandGenerator.STATE_PAUSE:
					CancelTimeout();
					// 暂停
					allowedUpdate = false; // 清掉，不然重新开始的时候会接着之前状态发送数据
					isUpdating = false;
					isUpdatePause = true;
					NotifyOnPause();
					break;
				case OtaCommandGenerator.STATE_CONTINUE:
					NotifyOnContinue();
					// 恢复
					isUpdating = true;
					isUpdatePause = false;
					GetOtaInfoVersion();
					break;
				case OtaCommandGenerator.STATE_TWS_DISCONNECTED:
					CancelTimeout();
					isUpdating = false;
					isTwsConnected = false;
					NotifyTWSDisconnected();
					NotifyOnReceiveTWSConnected(isTwsConnected.Value);
					break;
				case OtaCommandGenerator.STATE_ERROR_STOP:
					CancelTimeout();
					Release();
					NotifyOnErrorStop();
					break;

				case OtaCommandGenerator.STATE_ERR_SEQ:
					// 如果没有暂停，且还没发完，继续发；发完了就等STATE_DONE
					if (!isUpdatePause && !dataProvider.IsAllDataSent())
					{
						ReSendOtaStart();
					}
					break;
				default:
					FailFromDevice(status, "升级失败，请检查连接后重试。若仍失败请联系我们。");
					break;
			}
		}

		private void FailFromDevice(byte status, string userMessage)
		{
			CancelTimeout();
			OtaError.REPORT_FROM_DEVICE.DeviceErrorCode = status;
			OtaError.REPORT_FROM_DEVICE.SetUserMessage(userMessage);
			NotifyOnError(OtaError.REPORT_FROM_DEVICE);
			mainWindow.Dispatcher.InvokeAsync(() => mainWindow.LogMessage(userMessage));
		}

		protected void ProcessGetInfo(byte cmdSubType, byte[] extraData)
		{
			// 处理命令
			ProcessInfo(cmdSubType, extraData);

			// 处理完命令后的操作
			switch (cmdSubType)
			{
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_VERSION:
					GetOtaInfoUpdate(otaFirmwareVersion);
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_UPDATE:
					// 地址+是否升级
					if (allowedUpdate)
					{
						// 这里开始发第一包
						SendOtaStart();
					}
					else
					{
						CancelTimeout();
						NotifyOnError(OtaError.REFUSED_BY_DEVICE);
					}
					break;
			}
		}

		private void ProcessGetInfoTLV(byte[] extraData)
		{
			using (System.IO.MemoryStream ms = new System.IO.MemoryStream(extraData))
			using (System.IO.BinaryReader br = new System.IO.BinaryReader(ms))
            while (ms.Position < ms.Length - 2)
            {
                byte infoType = br.ReadByte();
                byte infoLength = br.ReadByte();
                byte[] infoData = br.ReadBytes(infoLength);
                ProcessInfo(infoType, infoData);
            }

			CheckIfReadyToUpdate();
		}

		// todo: 拆分
		// 处理设备回传信息
		private void ProcessInfo(byte infoType, byte[] infoData)
		{
			Console.WriteLine($"{TAG}: processInfo: {infoType} -> {BitConverter.ToString(infoData)}");

			switch (infoType)
			{
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_VERSION:
					if (infoData.Length == 2)
					{
						using (MemoryStream ms = new MemoryStream(infoData))
						using (BinaryReader br = new BinaryReader(ms))
						{
							int version = br.ReadInt16();
							NotifyOnReceiveVersion(version);

							deviceFirmwareVersion = version;
						}
					}
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_UPDATE:
					if (infoData.Length == 11)
					{
						using (MemoryStream ms = new MemoryStream(infoData))
						using (BinaryReader br = new BinaryReader(ms))
						{
							// 升级起始地址
							int startAddress = br.ReadInt32();
							dataProvider.SetStartAddress(startAddress);
							Console.WriteLine($"{TAG}: startAddress = {startAddress}");
							// 升级块大小
							int blockSize = br.ReadInt32();
							mBlockSize = blockSize;
							dataProvider.SetBlockSize(blockSize);
							Console.WriteLine($"{TAG}: blockSize = {blockSize}");
							// 包最大长度
							short packetSize = br.ReadInt16();
							mPacketSize = packetSize;
							dataProvider.SetPacketSize(packetSize);
							Console.WriteLine($"{TAG}: packetSize = {packetSize}");

							// 设备是否允许升级
							allowedUpdate = br.ReadByte() == 1;
							Console.WriteLine($"{TAG}: allowedUpdate = {allowedUpdate}");
						}
					}
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_CAPABILITIES:
					// 设备能力
					if (infoData.Length == 2)
					{
						using (MemoryStream ms = new MemoryStream(infoData))
						using (BinaryReader br = new BinaryReader(ms))
						{
							short dev_info_capabilities = br.ReadInt16();
							// 是否是TWS，TWS状态
							isTwsDevice = (dev_info_capabilities & OtaCommandGenerator.INFO_CAPABILITIES_TWS) != 0;
							isAllowedOta = (dev_info_capabilities & OtaCommandGenerator.INFO_CAPABILITIES_OTA) != 0;
							Console.WriteLine($"{TAG}: isTWS: {isTwsDevice}");
							Console.WriteLine($"{TAG}: isAllowedOTA: {isAllowedOta}");
							NotifyOnReceiveIsTWS(isTwsDevice.Value);
						}
					}
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_STATUS:
					if (infoData.Length == 2)
					{
						using (MemoryStream ms = new MemoryStream(infoData))
						using (BinaryReader br = new BinaryReader(ms))
						{
							short device_status = br.ReadInt16();
							// TWS是否已经连接
							isTwsConnected = (device_status & OtaCommandGenerator.INFO_STATUS_TWS_CONNECTED) != 0;
							Console.WriteLine($"{TAG}: isTwsConnected: {isTwsConnected}");
							NotifyOnReceiveTWSConnected(isTwsConnected.Value);
						}
					}
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_CHANNEL:
					if (infoData.Length == 1)
					{
						byte channel = infoData[0];
						if (channel == OtaCommandGenerator.INFO_CHANNEL_LEFT)
						{
							Console.WriteLine($"{TAG}: Channel: Left");
							NotifyOnReceiveChannel(true);
						}
						else if (channel == OtaCommandGenerator.INFO_CHANNEL_RIGHT)
						{
							Console.WriteLine($"{TAG}: Channel: Right");
							NotifyOnReceiveChannel(false);
						}
					}
					break;
				case OtaCommandGenerator.CMD_GET_INFO_TYPE_COMVERSION:
					if(infoData.Length == 2)
					{
						// 解析通信协议版本号
						string comVersion = $"{infoData[0]}.{infoData[1]}";
						Console.WriteLine($"{TAG}: Communication protocol version: {comVersion}");
						NotifyOnReceiveComVersion(comVersion);
					}
					break;

			}
		}

		/**
         * 发送数据
         */
		protected void BtSendData(byte[] data)
		{
			if(isUpdatePause) return;
			mainWindow.SendPayload_OTA(data);
		}

		/* Getter & Setter */

		public bool NeedIdentification()
		{
			return _needIdentification;
		}

		/**
         * 是否需要发送识别码，默认是发送。
         * 3月之后发的固件均需要使用识别码；更早的固件不需要发送，需要设为false，否则会报0x40错误。
         */
		public void SetNeedIdentification(bool needIdentification)
		{
			this._needIdentification = needIdentification;
		}

		/**
         * 传入OTA固件版本
         * @param otaFirmwareVersion 固件版本，0xFFFF表示强制升级（大于除本身外其他一切16位整数）
         */
		public void SetOtaFirmwareVersion(int otaFirmwareVersion)
		{
			this.otaFirmwareVersion = otaFirmwareVersion;
		}

		public class MyEventListener : EventListener
		{
			public void OnOtaReady()
			{
				Console.WriteLine("OTA已经准备就绪，可以开始升级");
			}

			public void OnOtaStart()
			{
				Console.WriteLine("OTA已开始");
			}

			public void OnOtaProgress(int progress)
			{
				Console.WriteLine($"OTA进度: {progress}%");
			}

			public void OnOtaStop()
			{
				Console.WriteLine("OTA已停止");
			}

			public void OnOtaFinish()
			{
				Console.WriteLine("OTA已全部完成 等待设备重启");
			}

			public void OnOtaPause()
			{
				Console.WriteLine("OTA已暂停");
			}

			public void OnOtaContinue()
			{
				Console.WriteLine("OTA已继续");
			}

			public void OnOtaError(OtaError error)
			{
				Console.WriteLine($"OTA遇到错误: {error.Description}");
			}

			public void OnTWSDisconnected()
			{
				Console.WriteLine("TWS断开事件");
			}

			public void OnReceiveVersion(int version)
			{
				Console.WriteLine($"接收到设备当前固件版本号: {version}");
			}

			public void OnReceiveIsTWS(bool isTWS)
			{
				Console.WriteLine($"是否TWS: {isTWS}");
			}

			public void OnReceiveTWSConnected(bool connected)
			{
				Console.WriteLine($"TWS对耳是否已连接: {connected}");
			}

			public void OnReceiveChannel(bool isLeftChannel)
			{
				Console.WriteLine($"声音左右通道: {(isLeftChannel ? "左声道" : "右声道")}");
			}

			public void OnReceiveComVersion(string comVersion)
			{
				Console.WriteLine($"接收到通信协议版本号: {comVersion}");
			}
		}

		/* Event Listener */
		/**
         * OTA事件监听器
         */
		public interface EventListener
		{
			/* OTA状态 */

			/**
             * OTA已经准备就绪，可以开始升级
             */
			void OnOtaReady();

			/**
             * OTA已开始
             */
			void OnOtaStart();

			/**
             * OTA进度
             * @param progress 进度
             */
			void OnOtaProgress(int progress);

			/**
             * OTA已停止
             */
			void OnOtaStop();

			/**
             * OTA已全部完成
             */
			void OnOtaFinish();

			/**
             * OTA已暂停
             */
			void OnOtaPause();

			/**
             * OTA已继续
             */
			void OnOtaContinue();

			/**
             * OTA遇到错误
             * @param error {@link OtaError}
             */
			void OnOtaError(OtaError error);

			/* 设备状态 */

			/**
             * TWS断开事件
             */
			void OnTWSDisconnected();

			/**
             * 接收到设备当前固件版本号
             * @param version 固件版本号，具体定义参见固件端说明文档
             */
			void OnReceiveVersion(int version);

			/**
             * 是否TWS
             * @param isTWS 是否TWS
             */
			void OnReceiveIsTWS(bool isTWS);

			/**
             * TWS对耳是否已连接
             * @param connected 是否已连接
             */
			void OnReceiveTWSConnected(bool connected);

			/**
             * 声音左右通道，非TWS不用关注
             * @param isLeftChannel true为左声道，false为右声道
             */
			void OnReceiveChannel(bool isLeftChannel);
			// 新增方法声明，用于接收通信协议版本号
			void OnReceiveComVersion(string comVersion);
		}
	}
}
