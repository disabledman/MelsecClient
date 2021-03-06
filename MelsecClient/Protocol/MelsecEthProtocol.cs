using System.Collections.Generic;

namespace System.Net.Melsec
{
    public abstract class MelsecEthProtocol : MelsecProtocol, IDisposable
    {
        private IPEndPoint EndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5000);
        private readonly int ErrorCodePosition;
        private readonly int MinResponseLength;
        protected readonly int ReturnValuePosition;
        private readonly byte ReturnPacketHeader;
        private readonly byte DataLengthPosition;
        protected byte NetNo;
        protected byte PcNo = 0xFF;
        protected byte destinationCpu = (byte)DestinationCpu.LocalStation;
        private IChannel Channel;
        protected byte[] PacketHead;

        protected MelsecEthProtocol(
            string ip,
            ushort port,
            int errorCodePosition,
            int minResponseLength,
            int returnValuePosition,
            byte returnPacketHeader,
            byte dataLengthPosition)
        {
            EndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            ErrorCodePosition = errorCodePosition;
            MinResponseLength = minResponseLength;
            ReturnValuePosition = returnValuePosition;
            ReturnPacketHeader = returnPacketHeader;
            DataLengthPosition = dataLengthPosition;
        }

        public string Ip
        {
            get
            {
                return EndPoint.Address.ToString();
            }
            set
            {
                CloseChannel();
                EndPoint.Address = IPAddress.Parse(value);
            }
        }

        public ushort Port
        {
            get
            {
                return (ushort)EndPoint.Port;
            }
            set
            {
                CloseChannel();
                if (value > 0)
                    EndPoint.Port = value;
                else throw new Exception("Port number must be greater than zero");
            }
        }

        public DestinationCpu DestinationCpu
        {
            get
            {
                return (DestinationCpu)destinationCpu;
            }
            set
            {
                destinationCpu = (byte)value;
            }
        }

        private bool useTcp;

        public bool UseTcp
        {
            get
            {
                return useTcp;
            }
            set
            {
                useTcp = value;
                CloseChannel();
            }
        }

        public bool KeepConnection { get; set; }

        private void InitChannel()
        {
            if (Channel == null)
            {
                if (!UseTcp)
                {
                    Channel = new UdpChannel(EndPoint);
                }
                else
                {
                    Channel = new TcpChannel(EndPoint);
                }
                Channel.SendTimeout = SendTimeout;
                Channel.ReceiveTimeout = ReceiveTimeout;
            }
        }

        private void CloseChannel()
        {
            if (Channel != null)
            {
                Channel.Dispose();
                Channel = null;
            }
        }

        protected override byte[] SendBuffer(byte[] buffer)
        {
            byte[] outBuff = new byte[0];
            InitChannel();
            outBuff = Channel.Execute(buffer);
            if (!KeepConnection)
                CloseChannel();
            if (outBuff.Length > MinResponseLength)
            {
                if (outBuff[0] != ReturnPacketHeader)
                    throw new Exception(string.Format("Response header PLC is corrupt: {0:X2} ({0}) <> {1:X2} ({1})",
                                                                    ReturnPacketHeader, outBuff[0]));
                LastError = BitConverter.ToUInt16(outBuff, ErrorCodePosition);
                if (LastError != 0)
                    throw new Exception(string.Format("PLC return error code: 0x{0:X4} ({0})", LastError));
                int lenght = BitConverter.ToInt16(outBuff, DataLengthPosition) + ErrorCodePosition;
                if (lenght != outBuff.Length)
                    throw new Exception("PLC returned buffer is corrupt");
            }
            else throw new Exception(string.Format("PLC returned buffer is too small: {0}", outBuff.Length));
            return outBuff;
        }

        public override string ToString()
        {
            return string.Format("{0}:{1} 0x{2:X2}:0x{3:X2}:0x{4:X2}",
                EndPoint.Address, EndPoint.Port, NetNo, PcNo, DestinationCpu);
        }

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    if (Channel != null)
                        Channel.Dispose();
                }
                disposed = true;
            }
        }

        ~MelsecEthProtocol()
        {
            Dispose(false);
        }

        private int CheckTypeSize<T>(byte min = 2, byte max = 4)
        {
            int typeSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(T));
            if (typeSize < min || typeSize > max)
                throw new Exception(Constants.WRONG_TYPE_SIZE);
            return typeSize;
        }

        public override T[] BatchReadWord<T>(ushort point, MelsecDeviceType DeviceType, ushort count)
        {
            int typeSize = CheckTypeSize<T>();
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            byte[] addr = GetPointBytes(point);
            byte[] cnt = GetPointCount(count * typeSize / 2);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0C,0x00,0x10,0x00,
                0x01,0x04,0x00,0x00,
                addr[0],addr[1],addr[2],
                (byte)DeviceType,
                cnt[0],cnt[1]};
            packet.AddRange(buff1);
            byte[] recvbuffer = SendBuffer(packet.ToArray());
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            int retLen = dataLen / typeSize;
            T[] ret = new T[retLen];
            Buffer.BlockCopy(recvbuffer, ReturnValuePosition, ret, 0, dataLen);
            return ret;
        }

        public override void BatchWriteWord<T>(ushort point, T[] val, MelsecDeviceType DeviceType)
        {
            int typeSize = CheckTypeSize<T>();
            if (val.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            byte[] addr = GetPointBytes(point);
            ushort count = (ushort)(val.Length * typeSize / 2);
            byte[] cnt = GetPointCount(count);
            byte[] len = GetRequestDataLength(19 + PacketHead.Length + count * 2 - ErrorCodePosition);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x01,0x14,0x00,0x00,addr[0],addr[1],addr[2],(byte)DeviceType,cnt[0],cnt[1]};
            packet.AddRange(buff1);
            byte[] buff2 = new byte[count * 2];
            Buffer.BlockCopy(val, 0, buff2, 0, buff2.Length);
            packet.AddRange(buff2);
            SendBuffer(packet.ToArray());
        }

        public override T[] RandomReadWord<T>(ushort[] point, MelsecDeviceType DeviceType)
        {
            int typeSize = CheckTypeSize<T>();
            if (point.Length == 0)
                throw new Exception(Constants.NO_DATA_READ);
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            ushort count = (ushort)(point.Length);
            byte[] len = GetRequestDataLength(15 + PacketHead.Length + count * 4 - ErrorCodePosition);
            byte[] cnt = GetPointCount(count);
            if (typeSize == 4)
            {
                cnt[0] = 0;
                cnt[1] = (byte)count;
            }
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x03,0x04,0x00,0x00,cnt[0],cnt[1]};
            packet.AddRange(buff1);
            for (int i = 0; i < count; ++i)
            {
                byte[] addr = GetPointBytes((ushort)(point[i]));
                byte[] buff2 = { addr[0], addr[1], addr[2], (byte)DeviceType };
                packet.AddRange(buff2);
            }
            byte[] recvbuffer = SendBuffer(packet.ToArray());
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            int retLen = dataLen / typeSize;
            T[] ret = new T[retLen];
            Buffer.BlockCopy(recvbuffer, ReturnValuePosition, ret, 0, dataLen);
            return ret;
        }

        public override void RandomWriteWord<T>(ushort[] point, T[] val, MelsecDeviceType DeviceType)
        {
            int typeSize = CheckTypeSize<T>();
            if (point.Length != val.Length)
                throw new Exception(Constants.SIZE_MISMATCH);
            if (val.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            ushort count = (ushort)point.Length;
            byte[] len = GetRequestDataLength(15 + PacketHead.Length + count * (4 + typeSize) - ErrorCodePosition);
            byte[] cnt = GetPointCount(count);
            if (typeSize == 4)
            {
                cnt[0] = 0;
                cnt[1] = (byte)count;
            }
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x02,0x14,0x00,0x00,cnt[0],cnt[1]};
            packet.AddRange(buff1);
            for (int i = 0; i < count; ++i)
            {
                byte[] addr = GetPointBytes(point[i]);
                byte[] rval = new byte[typeSize];
                Buffer.BlockCopy(val, i * typeSize, rval, 0, typeSize);
                byte[] buff2 = { addr[0], addr[1], addr[2], (byte)DeviceType };
                packet.AddRange(buff2);
                packet.AddRange(rval);
            }
            SendBuffer(packet.ToArray());
        }

        public override T[] ReadBuffer<T>(int address, byte count)
        {
            int typeSize = CheckTypeSize<T>();
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            byte[] addr = GetBytes(address, 4);
            byte[] cnt = GetPointCount(count * typeSize / 2);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0C,0x00,0x10,0x00,
                0x13,0x06,0x00,0x00,
                addr[0],addr[1],addr[2],addr[3],
                cnt[0],cnt[1]};
            packet.AddRange(buff1);
            byte[] recvbuffer = SendBuffer(packet.ToArray());
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            int retLen = dataLen / typeSize;
            T[] ret = new T[retLen];
            Buffer.BlockCopy(recvbuffer, ReturnValuePosition, ret, 0, dataLen);
            return ret;
        }

        public override void WriteBuffer<T>(int address, T[] val)
        {
            if (val.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            int typeSize = CheckTypeSize<T>();
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            byte[] addr = GetBytes(address, 4);
            ushort count = (ushort)(val.Length * typeSize / 2);
            byte[] cnt = GetPointCount(count);
            byte[] len = GetRequestDataLength(19 + PacketHead.Length + count * 2 - ErrorCodePosition);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x13,0x16,0x00,0x00,addr[0],addr[1],addr[2],addr[3],cnt[0],cnt[1]};
            packet.AddRange(buff1);
            byte[] buff2 = new byte[count * 2];
            Buffer.BlockCopy(val, 0, buff2, 0, buff2.Length);
            packet.AddRange(buff2);
            SendBuffer(packet.ToArray());
        }

        public override T[] ReadIntelliBuffer<T>(ushort module, int headAddress, int address, byte count)
        {
            List<byte> packet = new List<byte>();
            int typeSize = CheckTypeSize<T>(min: 1);
            packet.AddRange(PacketHead);
            byte[] mod = GetBytes(module, 2);
            byte[] addr = GetBytes(headAddress + address * 2, 4);
            byte[] cnt = GetPointCount(count * typeSize);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0E,0x00,0x10,0x00,
                0x01,0x06,0x00,0x00,
                addr[0],addr[1],addr[2],addr[3],
                cnt[0],cnt[1],
                mod[0],mod[1]};
            packet.AddRange(buff1);
            byte[] recvbuffer = SendBuffer(packet.ToArray());
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            int retLen = dataLen / typeSize;
            T[] ret = new T[retLen];
            Buffer.BlockCopy(recvbuffer, ReturnValuePosition, ret, 0, dataLen);
            return ret;
        }

        public override void WriteIntelliBuffer<T>(ushort module, int headAddress, int address, T[] val)
        {
            if (val.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            int typeSize = CheckTypeSize<T>(min: 1);
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            byte[] mod = GetBytes(module, 2);
            byte[] addr = GetBytes(headAddress + address * 2, 4);
            ushort count = (ushort)(val.Length * typeSize);
            byte[] cnt = GetPointCount(count);
            byte[] len = GetRequestDataLength(21 + PacketHead.Length + count - ErrorCodePosition);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x01,0x16,0x00,0x00,addr[0],addr[1],addr[2],addr[3],cnt[0],cnt[1],mod[0],mod[1]};
            packet.AddRange(buff1);
            byte[] buff2 = new byte[count];
            Buffer.BlockCopy(val, 0, buff2, 0, buff2.Length);
            packet.AddRange(buff2);
            SendBuffer(packet.ToArray());
        }

        public override string ReadCPUModelName()
        {
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x06,0x00,0x10,0x00,
                0x01,0x01,
                0x00,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            byte[] recvbuffer = SendBuffer(sendbuffer);
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            byte[] name = new byte[dataLen];
            Buffer.BlockCopy(recvbuffer, ReturnValuePosition, name, 0, dataLen - 2);
            string ret = System.Text.Encoding.UTF8.GetString(name);
            return ret;
        }

        public override float ReadReal(ushort point, MelsecDeviceType DeviceType)
        {
            return ReadReal(point, DeviceType, 1)[0];
        }

        public override float[] ReadReal(ushort point, MelsecDeviceType DeviceType, byte count)
        {
            return BatchReadWord<float>(point, DeviceType, count);
        }

        public override float[] ReadReal(ushort[] point, MelsecDeviceType DeviceType)
        {
            return RandomReadWord<float>(point, DeviceType);
        }

        public override void WriteReal(ushort point, float val, MelsecDeviceType DeviceType)
        {
            WriteReal(point, new float[] { val }, DeviceType);
        }

        public override void WriteReal(ushort point, float[] val, MelsecDeviceType DeviceType)
        {
            BatchWriteWord<float>(point, val, DeviceType);
        }

        public override void WriteReal(ushort[] point, float[] val, MelsecDeviceType DeviceType)
        {
            RandomWriteWord<float>(point, val, DeviceType);
        }

        public override uint ReadDword(ushort point, MelsecDeviceType DeviceType)
        {
            return ReadDword(point, DeviceType, 1)[0];
        }

        public override uint[] ReadDword(ushort point, MelsecDeviceType DeviceType, byte count)
        {
            return BatchReadWord<uint>(point, DeviceType, count);
        }

        public override uint[] ReadDword(ushort[] point, MelsecDeviceType DeviceType)
        {
            return RandomReadWord<uint>(point, DeviceType);
        }

        public override void WriteDword(ushort point, uint val, MelsecDeviceType DeviceType)
        {
            WriteDword(point, new uint[] { val }, DeviceType);
        }

        public override void WriteDword(ushort point, uint[] val, MelsecDeviceType DeviceType)
        {
            BatchWriteWord<uint>(point, val, DeviceType);
        }

        public override void WriteDword(ushort[] point, uint[] val, MelsecDeviceType DeviceType)
        {
            RandomWriteWord<uint>(point, val, DeviceType);
        }

        public override ushort ReadWord(ushort point, MelsecDeviceType DeviceType)
        {
            return ReadWord(point, DeviceType, 1)[0];
        }

        public override ushort[] ReadWord(ushort point, MelsecDeviceType DeviceType, byte count)
        {
            return BatchReadWord<ushort>(point, DeviceType, count);
        }

        public override ushort[] ReadWord(ushort[] point, MelsecDeviceType DeviceType)
        {
            return RandomReadWord<ushort>(point, DeviceType);
        }

        public override void WriteWord(ushort point, ushort val, MelsecDeviceType DeviceType)
        {
            WriteWord(point, new ushort[] { val }, DeviceType);
        }

        public override void WriteWord(ushort point, ushort[] val, MelsecDeviceType DeviceType)
        {
            BatchWriteWord<ushort>(point, val, DeviceType);
        }

        public override void WriteWord(ushort[] point, ushort[] val, MelsecDeviceType DeviceType)
        {
            RandomWriteWord<ushort>(point, val, DeviceType);
        }

        public override bool ReadByte(ushort point, MelsecDeviceType DeviceType)
        {
            return ReadByte(point, DeviceType, 1)[0];
        }

        public override bool[] ReadByte(ushort point, MelsecDeviceType DeviceType, byte count)
        {
            byte[] addr = GetPointBytes(point);
            byte[] cnt = GetPointCount(count);
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0C,0x00,0x10,0x00,
                0x01,0x04,0x01,0x00,
                addr[0],addr[1],addr[2],
                (byte)DeviceType,
                cnt[0],cnt[1]};
            sendbuffer = Concat(PacketHead, sendbuffer);
            byte[] recvbuffer = SendBuffer(sendbuffer);
            int dataLen = recvbuffer.Length - ReturnValuePosition;
            int retLen = dataLen * 2;
            bool[] ret = new bool[retLen];
            for (int i = 0, j = 0; i < dataLen; ++i, j += 2)
            {
                byte recvByte = recvbuffer[ReturnValuePosition + i];
                byte[] retB = new byte[1];
                retB[0] = (byte)(recvByte >> 4);
                ret[j] = BitConverter.ToBoolean(retB, 0);
                retB[0] = (byte)(recvByte & 1);
                ret[j + 1] = BitConverter.ToBoolean(retB, 0);
            }
            return ret;
        }

        public override bool[] ReadByte(ushort[] point, MelsecDeviceType DeviceType)
        {
            if (point.Length == 0)
                throw new Exception(Constants.NO_DATA_READ);
            ushort[] us = ReadWord(point, DeviceType);
            bool[] ret = new bool[us.Length];
            for (int i = 0; i < ret.Length; ++i)
            {
                ret[i] = ((us[i] & 1) == 1);
            }
            return ret;
        }

        public override void WriteByte(ushort point, bool state, MelsecDeviceType DeviceType)
        {
            byte[] addr = GetPointBytes(point);
            byte On;
            if (state) On = 0x10;
            else On = 0x00;
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0D,0x00,0x10,0x00,
                0x01,0x14,0x01,0x00,
                addr[0],addr[1],addr[2],
                (byte)DeviceType,
                0x01,0x00,
                On};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }

        public override void WriteByte(ushort point, bool[] state, MelsecDeviceType DeviceType)
        {
            if (state.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            if (state.Length == 1)
            {
                WriteByte(point, state[0], DeviceType);
            }
            else
            {
                ushort count = (ushort)state.Length;
                if ((count & 1) != 0)
                {
                    throw new Exception(Constants.ODD_SIZE_ARRAY);
                }
                List<byte> packet = new List<byte>();
                packet.AddRange(PacketHead);
                byte[] addr = GetPointBytes(point);
                byte[] cnt = GetPointCount(count);
                byte[] len = GetRequestDataLength(19 + PacketHead.Length + count / 2 - ErrorCodePosition);
                byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                                               0x01,0x14,0x01,0x00,addr[0],addr[1],addr[2],(byte)DeviceType, cnt[0],cnt[1]};
                packet.AddRange(buff1);
                for (int i = 0, j = 0; i < count; i += 2, ++j)
                {
                    byte[] buff2 = new byte[1];
                    if (state[i]) buff2[0] |= 0x10;
                    if (state[i + 1]) buff2[0] |= 0x01;
                    packet.AddRange(buff2);
                }
                SendBuffer(packet.ToArray());
            }
        }

        public override void WriteByte(ushort[] point, bool[] state, MelsecDeviceType DeviceType)
        {
            if (point.Length != state.Length)
                throw new Exception(Constants.SIZE_MISMATCH);
            if (state.Length == 0)
                throw new Exception(Constants.NO_DATA_WRITE);
            List<byte> packet = new List<byte>();
            packet.AddRange(PacketHead);
            ushort count = (ushort)point.Length;
            byte[] len = GetRequestDataLength(14 + PacketHead.Length + count * 5 - ErrorCodePosition);
            byte[] buff1 = {NetNo,PcNo,destinationCpu,0x03,0x00,len[0],len[1],0x10,0x00,
                0x02,0x14,0x01,0x00,(byte)count};
            packet.AddRange(buff1);
            for (int i = 0; i < count; ++i)
            {
                byte[] addr = GetPointBytes(point[i]);
                byte[] bval = new byte[1];
                if (state[i]) bval[0] = 1;
                byte[] buff2 = { addr[0], addr[1], addr[2], (byte)DeviceType, bval[0] };
                packet.AddRange(buff2);
            }
            SendBuffer(packet.ToArray());
        }

        public void ErrLedOff()
        {
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x06,0x00,0x10,0x00,
                0x17,0x16,
                0x00,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }

        public override void Run(bool forced, ClearMode mode)
        {
            byte frcd = (forced) ? frcd = 0x03 : frcd = 0x01;
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x0A,0x00,0x10,0x00,
                0x01,0x10,
                0x00,0x00,
                frcd,0x00,
                (byte)mode, 0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }

        public override void Pause(bool forced)
        {
            byte frcd = (forced) ? frcd = 0x03 : frcd = 0x01;
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x08,0x00,0x10,0x00,
                0x03,0x10,
                0x00,0x00,
                frcd,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }

        public override void Stop()
        {
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x08,0x00,0x10,0x00,
                0x02,0x10,
                0x00,0x00,
                0x01,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }

        public override void Reset()
        {
            byte[] sendbuffer = {NetNo,PcNo,destinationCpu,0x03,0x00,0x08,0x00,0x10,0x00,
                0x06,0x10,
                0x00,0x00,
                0x01,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            try
            {
                SendBuffer(sendbuffer);
            }
            catch { }
        }

        public override void LatchClear()
        {
            var sendbuffer = new byte[] {NetNo,PcNo,destinationCpu,0x03,0x00,0x08,0x00,0x10,0x00,
                0x05,0x10,
                0x00,0x00,
                0x01,0x00};
            sendbuffer = Concat(PacketHead, sendbuffer);
            SendBuffer(sendbuffer);
        }
    }
}