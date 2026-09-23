using System.Globalization;
using System.Linq;

namespace BicoherenceAnalyzer; 

/// <summary>
/// BDF/EDF/EDF+ 文件读取器
/// 
/// 支持三种格式:
///   - BDF (Biosemi): 24位采样，3字节/样本，Biosemi 字段打包通道头
///   - EDF: 16位采样，2字节/样本，标准每通道256字节通道头
///   - EDF+: 同 EDF，版本字段含"+"
/// 
/// 格式检测: 从固定头版本字段自动识别 (BDF version="BIOSEMI"/"BDF..."; EDF version="0"/"EDF+C")
/// </summary>
public class BdfReader : IDisposable
{
    // === BDF/EDF 固定文件头字段 ===
    public string Version { get; private set; } = "";
    public string PatientId { get; private set; } = "";
    public string RecordingId { get; private set; } = "";
    public int NumDataRecords { get; private set; }
    public double RecordDuration { get; private set; }
    public int NumSignals { get; private set; }

    // === 通道信息 ===
    public BdfChannel[] Channels { get; private set; } = Array.Empty<BdfChannel>();

    // === 格式标志 ===
    /// <summary>true = BDF (24位), false = EDF/EDF+ (16位)</summary>
    public bool IsBdf { get; private set; } = true;

    /// <summary>true = EDF+ (含 annotations 通道)</summary>
    public bool IsEdfPlus { get; private set; }

    /// <summary>每样本字节数 (BDF=3, EDF=2)</summary>
    public int BytesPerSample => IsBdf ? 3 : 2;

    /// <summary>数据格式标签</summary>
    public string FormatLabel => IsBdf ? "BDF" : (IsEdfPlus ? "EDF+" : "EDF");

    // === 内部状态 ===
    private readonly FileStream _fileStream;
    private readonly BinaryReader _reader;
    private readonly long _dataStartOffset;     // 数据区起始偏移量
    private readonly int _totalSamplesPerRecord; // 每条记录的总采样点数
    private readonly int _bytesPerRecord;        // 每条记录的字节数

    // === Biosemi BDF 默认校准值 (24位) ===
    private const double DefaultDigitalMin = -8388608.0;
    private const double DefaultDigitalMax = 8388607.0;
    private const double DefaultPhysicalMin = -262144.0;
    private const double DefaultPhysicalMax = 262144.0;

    // === EDF/EDF+ 默认校准值 (16位) ===
    private const double DefaultEdfDigitalMin = -32768.0;
    private const double DefaultEdfDigitalMax = 32767.0;
    // EDF 物理范围因设备而异，使用保守默认值
    private const double DefaultEdfPhysicalMin = -32768.0;
    private const double DefaultEdfPhysicalMax = 32767.0;

    /// <summary>
    /// 构造函数：打开 BDF/EDF/EDF+ 文件并解析文件头
    /// </summary>
    public BdfReader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"文件未找到: {filePath}");

        _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_fileStream);

        if (_fileStream.Length < 256)
            throw new InvalidDataException("文件小于256字节，无效格式");

        // 步骤1: 解析256字节固定文件头
        ParseFixedHeader();

        // 步骤2: 检测格式
        DetectFormat();

        // 步骤3: 读取整个文件头到内存
        int totalHeaderBytes = 256 + NumSignals * 256;
        byte[] fullHeader = new byte[totalHeaderBytes];
        _fileStream.Seek(0, SeekOrigin.Begin);
        _fileStream.Read(fullHeader, 0, totalHeaderBytes);

        // 步骤4: 解析通道头
        if (IsBdf)
            Channels = ParsePackedChannelHeaders(fullHeader, 256); // Biosemi 字段打包格式
        else
            Channels = ParseStandardChannelHeaders(fullHeader, 256); // 标准 EDF 每通道独立头

        // 步骤5: 设置数据区参数
        _dataStartOffset = totalHeaderBytes;
        _totalSamplesPerRecord = Channels.Sum(c => c.SamplesPerRecord);
        _bytesPerRecord = _totalSamplesPerRecord * BytesPerSample;

        // 验证文件大小
        if (NumDataRecords > 0)
        {
            long expectedSize = _dataStartOffset + (long)NumDataRecords * _bytesPerRecord;
            if (_fileStream.Length < expectedSize)
                Console.WriteLine($"[警告] 文件可能截断: 实际 {_fileStream.Length} < 预期 {expectedSize}");
        }

        Console.WriteLine($"[{FormatLabel}] 版本: {Version.Trim()}, 通道: {NumSignals}, 记录: {NumDataRecords}, " +
            $"记录时长: {RecordDuration}s, 数据起始: {_dataStartOffset}");
    }

    /// <summary>
    /// 从版本字段检测文件格式
    /// </summary>
    private void DetectFormat()
    {
        // 去除不可打印字符和空白
        string v = new string(Version.Where(c => !char.IsControl(c) && c != '?').ToArray()).Trim().ToUpperInvariant();
        // BDF: "BIOSEMI" 或 "BDF..."
        if (v.StartsWith("BIOSEMI") || v.StartsWith("BDF"))
        {
            IsBdf = true;
            IsEdfPlus = false;
        }
        else
        {
            IsBdf = false;
            // EDF+: 版本字段含 "+"
            IsEdfPlus = v.Contains("+");
        }
    }

    /// <summary>
    /// 解析BDF固定文件头（前256字节ASCII文本）
    /// </summary>
    private void ParseFixedHeader()
    {
        byte[] headerBytes = _reader.ReadBytes(256);
        if (headerBytes.Length < 256)
            throw new InvalidDataException("BDF文件头不足256字节");

        Version = ReadAscii(headerBytes, 0, 8).Trim();
        PatientId = ReadAscii(headerBytes, 8, 80).Trim();
        RecordingId = ReadAscii(headerBytes, 88, 80).Trim();

        string numRecordsStr = ReadAscii(headerBytes, 236, 8).Trim();
        NumDataRecords = int.Parse(numRecordsStr, CultureInfo.InvariantCulture);

        string recordDurStr = ReadAscii(headerBytes, 244, 8).Trim();
        RecordDuration = ParseDoubleSafe(recordDurStr);

        string numSignalsStr = ReadAscii(headerBytes, 252, 4).Trim();
        NumSignals = int.Parse(numSignalsStr, CultureInfo.InvariantCulture);

        // 验证文件头字节数
        string headerBytesStr = ReadAscii(headerBytes, 184, 8).Trim();
        int declaredHeaderBytes = int.Parse(headerBytesStr, CultureInfo.InvariantCulture);
        int expected = 256 + NumSignals * 256;
        if (declaredHeaderBytes != expected)
            Console.WriteLine($"[警告] 声明头字节({declaredHeaderBytes}) ≠ 预期({expected})");
    }

    /// <summary>
    /// 解析标准 EDF/EDF+ 通道头（每通道独立256字节块）
    /// 
    /// 与 Biosemi 打包格式不同，标准 EDF 每个通道的字段在连续的256字节中
    /// </summary>
    private static BdfChannel[] ParseStandardChannelHeaders(byte[] fullHeader, int headerStart)
    {
        int N = (fullHeader.Length - headerStart) / 256;
        var channels = new BdfChannel[N];

        for (int i = 0; i < N; i++)
        {
            int baseOffset = headerStart + i * 256;

            string label = ReadAscii(fullHeader, baseOffset, 16).Trim();
            string physDim = ReadAscii(fullHeader, baseOffset + 96, 8).Trim();
            string physMinStr = ReadAscii(fullHeader, baseOffset + 104, 8).Trim();
            string physMaxStr = ReadAscii(fullHeader, baseOffset + 112, 8).Trim();
            string digMinStr = ReadAscii(fullHeader, baseOffset + 120, 8).Trim();
            string digMaxStr = ReadAscii(fullHeader, baseOffset + 128, 8).Trim();
            string samplesStr = ReadAscii(fullHeader, baseOffset + 216, 8).Trim();

            double physMin = ParseDoubleSafe(physMinStr, DefaultEdfPhysicalMin);
            double physMax = ParseDoubleSafe(physMaxStr, DefaultEdfPhysicalMax);
            double digMin = ParseDoubleSafe(digMinStr, DefaultEdfDigitalMin);
            double digMax = ParseDoubleSafe(digMaxStr, DefaultEdfDigitalMax);

            if (Math.Abs(physMax - physMin) < 1e-6 || Math.Abs(digMax - digMin) < 1e-6)
            {
                physMin = DefaultEdfPhysicalMin;
                physMax = DefaultEdfPhysicalMax;
                digMin = DefaultEdfDigitalMin;
                digMax = DefaultEdfDigitalMax;
            }

            int samplesPerRec = 1;
            if (!string.IsNullOrEmpty(samplesStr))
                int.TryParse(samplesStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out samplesPerRec);

            channels[i] = new BdfChannel
            {
                Index = i,
                Label = label,
                PhysicalDimension = physDim,
                PhysicalMin = physMin,
                PhysicalMax = physMax,
                DigitalMin = digMin,
                DigitalMax = digMax,
                SamplesPerRecord = samplesPerRec
            };
        }

        return channels;
    }

    /// <summary>
    /// 解析 Biosemi 字段打包格式的通道头
    /// 
    /// 字段顺序（每种字段所有通道的值连续存放）:
    ///   Label(16B×N) → Transducer(80B×N) → PhysDim(8B×N) → PhysMin(8B×N) 
    ///   → PhysMax(8B×N) → DigMin(8B×N) → DigMax(8B×N) → Prefilter(80B×N)
    ///   → SamplesPerRec(8B×N) → Reserved(32B×N)
    /// </summary>
    private static BdfChannel[] ParsePackedChannelHeaders(byte[] fullHeader, int headerStart)
    {
        int N = (fullHeader.Length - headerStart) / 256;
        var channels = new BdfChannel[N];

        // 各字段在 packed header 中的起始偏移
        int offset = headerStart;
        int labelOffset = offset;                           offset += 16 * N;
        int transducerOffset = offset;                      offset += 80 * N;
        int physDimOffset = offset;                         offset += 8 * N;
        int physMinOffset = offset;                         offset += 8 * N;
        int physMaxOffset = offset;                         offset += 8 * N;
        int digMinOffset = offset;                          offset += 8 * N;
        int digMaxOffset = offset;                          offset += 8 * N;
        int prefilterOffset = offset;                       offset += 80 * N;
        int samplesOffset = offset;                         offset += 8 * N;
        // reservedOffset = offset;  (不需要解析保留字段)

        for (int i = 0; i < N; i++)
        {
            string label = ReadAscii(fullHeader, labelOffset + i * 16, 16).Trim();
            string physDim = ReadAscii(fullHeader, physDimOffset + i * 8, 8).Trim();
            string physMinStr = ReadAscii(fullHeader, physMinOffset + i * 8, 8).Trim();
            string physMaxStr = ReadAscii(fullHeader, physMaxOffset + i * 8, 8).Trim();
            string digMinStr = ReadAscii(fullHeader, digMinOffset + i * 8, 8).Trim();
            string digMaxStr = ReadAscii(fullHeader, digMaxOffset + i * 8, 8).Trim();
            string samplesStr = ReadAscii(fullHeader, samplesOffset + i * 8, 8).Trim();

            // 解析校准值（使用容错解析，非数字时使用默认值）
            double physMin = ParseDoubleSafe(physMinStr, DefaultPhysicalMin);
            double physMax = ParseDoubleSafe(physMaxStr, DefaultPhysicalMax);
            double digMin = ParseDoubleSafe(digMinStr, DefaultDigitalMin);
            double digMax = ParseDoubleSafe(digMaxStr, DefaultDigitalMax);

            // 如果校准值异常（如physMin==physMax），使用默认值
            if (Math.Abs(physMax - physMin) < 1e-6 || Math.Abs(digMax - digMin) < 1e-6)
            {
                physMin = DefaultPhysicalMin;
                physMax = DefaultPhysicalMax;
                digMin = DefaultDigitalMin;
                digMax = DefaultDigitalMax;
            }

            int samplesPerRec = 1;
            if (!string.IsNullOrEmpty(samplesStr))
                int.TryParse(samplesStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out samplesPerRec);

            channels[i] = new BdfChannel
            {
                Index = i,
                Label = label,
                PhysicalDimension = physDim,
                PhysicalMin = physMin,
                PhysicalMax = physMax,
                DigitalMin = digMin,
                DigitalMax = digMax,
                SamplesPerRecord = samplesPerRec
            };
        }

        return channels;
    }

    /// <summary>
    /// 获取通道名称列表
    /// </summary>
    public string[] GetChannelNames() => Channels.Select(c => c.Label).ToArray();

    /// <summary>
    /// 读取指定通道的全部EEG数据（转换为物理值 μV）
    /// </summary>
    public double[] ReadChannelData(int channelIndex)
    {
        return ReadChannelData(channelIndex, 0,
            NumDataRecords * Channels[channelIndex].SamplesPerRecord);
    }

    /// <summary>
    /// 读取指定通道在指定采样点范围内的EEG数据（μV）
    /// </summary>
    public double[] ReadChannelData(int channelIndex, int startSample, int sampleCount)
    {
        var channel = Channels[channelIndex];
        int sps = channel.SamplesPerRecord;  // 该通道每记录的采样点数
        int totalSamples = NumDataRecords * sps;

        // 边界检查
        if (startSample < 0) startSample = 0;
        if (startSample + sampleCount > totalSamples)
            sampleCount = totalSamples - startSample;
        if (sampleCount <= 0)
            return Array.Empty<double>();

        double[] data = new double[sampleCount];

        // 计算该通道之前所有通道的样本偏移
        int precedingSamples = 0;
        for (int i = 0; i < channelIndex; i++)
            precedingSamples += Channels[i].SamplesPerRecord;

        // 校准参数
        double digRange = channel.DigitalMax - channel.DigitalMin;
        double physRange = channel.PhysicalMax - channel.PhysicalMin;
        double digMin = channel.DigitalMin;
        double physMin = channel.PhysicalMin;

        int bps = BytesPerSample; // BDF=3, EDF=2

        // 确定需要读取的记录范围
        int firstRecord = startSample / sps;
        int lastRecord = (startSample + sampleCount - 1) / sps;
        if (lastRecord >= NumDataRecords)
            lastRecord = NumDataRecords - 1;

        // 缓冲区：每条记录的所有采样
        int recordBytes = _bytesPerRecord;
        byte[] recordBuf = new byte[recordBytes];

        int dataIdx = 0;
        int offsetInFirst = startSample - firstRecord * sps;

        for (int rec = firstRecord; rec <= lastRecord; rec++)
        {
            long recordOff = _dataStartOffset + (long)rec * recordBytes;
            _fileStream.Seek(recordOff, SeekOrigin.Begin);
            int bytesRead = _fileStream.Read(recordBuf, 0, recordBytes);
            if (bytesRead < recordBytes)
                break;

            // 该通道在本记录中的起始字节位置
            int chByteStart = precedingSamples * bps;

            int sStart = (rec == firstRecord) ? offsetInFirst : 0;
            int sEnd = sps;
            sEnd = Math.Min(sEnd, sps);

            for (int s = sStart; s < sEnd && dataIdx < sampleCount; s++)
            {
                int bytePos = chByteStart + s * bps;
                int raw = IsBdf ? ReadInt24LE(recordBuf, bytePos)
                                : ReadInt16LE(recordBuf, bytePos);
                data[dataIdx++] = (raw - digMin) / digRange * physRange + physMin;
            }
        }

        if (dataIdx < sampleCount)
            Array.Resize(ref data, dataIdx);

        return data;
    }

    /// <summary>
    /// 读取指定时间范围（秒）内的EEG数据
    /// </summary>
    public double[] ReadChannelDataByTime(int channelIndex, double startTimeSec,
        double durationSec, int samplingRate)
    {
        int startSample = (int)(startTimeSec * samplingRate);
        int sampleCount = (int)(durationSec * samplingRate);
        return ReadChannelData(channelIndex, startSample, sampleCount);
    }

    /// <summary>
    /// 查找状态/触发通道索引。
    /// Biosemi BDF 的状态通道通常标注为 "Status"，或位于所有 EEG 通道之后。
    /// </summary>
    public int FindStatusChannelIndex()
    {
        for (int i = Channels.Length - 1; i >= 0; i--)
        {
            string lbl = Channels[i].Label.ToUpperInvariant();
            if (lbl.Contains("STATUS") || lbl.Contains("TRIG") || lbl.Contains("TRIGGER"))
                return i;
        }
        // 未找到显式标签 → 返回最后一个通道（BS 惯例）
        return Channels.Length - 1;
    }

    /// <summary>
    /// 从 BDF 状态通道提取触发事件列表。
    /// 检测状态通道值的变化点（上升沿/非零值），转换为事件列表。
    /// </summary>
    /// <param name="samplingRate">用于时间计算的采样率 (Hz)</param>
    /// <returns>按时间排序的事件列表</returns>
    public List<(double OnsetSec, int Value)> ExtractTriggerEvents(int samplingRate)
    {
        int statusIdx = FindStatusChannelIndex();
        double[] statusData = ReadChannelData(statusIdx);
        var events = new List<(double OnsetSec, int Value)>();

        int prevVal = 0;
        for (int i = 0; i < statusData.Length; i++)
        {
            int curVal = (int)Math.Round(statusData[i]);
            // 检测变化：状态值从 0 变为非零（上升沿 = 触发开始）
            if (curVal != 0 && curVal != prevVal)
            {
                double onset = (double)i / samplingRate;
                events.Add((onset, curVal));
            }
            prevVal = curVal;
        }

        return events;
    }

    /// <summary>
    /// 读取24位有符号整数（小端序，符号扩展）
    /// </summary>
    private static int ReadInt24LE(byte[] buf, int offset)
    {
        int value = buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16);
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000);
        return value;
    }

    /// <summary>
    /// 读取16位有符号整数（小端序）— EDF/EDF+ 采样格式
    /// </summary>
    private static short ReadInt16LE(byte[] buf, int offset)
    {
        return (short)(buf[offset] | (buf[offset + 1] << 8));
    }

    /// <summary>从字节数组读取ASCII字符串</summary>
    private static string ReadAscii(byte[] buf, int offset, int len)
    {
        int actual = Math.Min(len, buf.Length - offset);
        return System.Text.Encoding.ASCII.GetString(buf, offset, actual);
    }

    /// <summary>安全解析double，失败返回默认值</summary>
    private static double ParseDoubleSafe(string s, double defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return result;
        return defaultValue;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _fileStream?.Dispose();
    }
}

/// <summary>
/// BDF通道信息
/// </summary>
public class BdfChannel
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public string PhysicalDimension { get; set; } = "";
    public double PhysicalMin { get; set; }
    public double PhysicalMax { get; set; }
    public double DigitalMin { get; set; }
    public double DigitalMax { get; set; }
    public int SamplesPerRecord { get; set; }
}
