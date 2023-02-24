

// See https://aka.ms/new-console-template for more information
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;



var curPath = "ARES" + DateTime.Now.ToString("yyyyMMddHHmmss");
Directory.CreateDirectory(curPath);


var host = "https://vip8.3sybf.com";


// 先下载m3u8文件
var m3u8FileUrl = "/20230220/tiQOavbA/index.m3u8";


HttpClient client = new HttpClient();
var m3u8FileBytes = await client.GetByteArrayAsync($"{host}{m3u8FileUrl}");
var baseFile = Path.Combine(curPath, "base.m3u8");
File.WriteAllBytes(baseFile, m3u8FileBytes);

var baseContent = await File.ReadAllLinesAsync(baseFile);
var baseContentList = baseContent.ToList();

// 多个媒体流
if (baseContentList.Any(x => x.StartsWith(M3U8Protocol.EXTM3U_STREAM_INF)))
{
    var index = 1;
    // 解析媒体流并下载
    for (int i = 0; i < baseContentList.Count(); i++)
    {
        if (baseContent[i].StartsWith(M3U8Protocol.EXTM3U_STREAM_INF))
        {
            var subUrl = baseContent[i + 1];
            // 创建目录并下载m3u8文件
            var subM3u8FileBytes = await (new HttpClient()).GetByteArrayAsync($"{host}{subUrl}");
            File.WriteAllBytes(Path.Combine(curPath, $"base{index.ToString()}.m3u8"), subM3u8FileBytes);
            index++;
        }
    }
    //删除base文件
    File.Delete(baseFile);
}


// 读取当前文件夹内的m3u8文件
var m3u8BaseFiles = Directory.EnumerateFiles(curPath, "*.m3u8").ToList();


foreach (var meu8File in m3u8BaseFiles)
{
    List<DownloadFile> downloadFiles = new List<DownloadFile>();

    var meu8FileContent = await File.ReadAllLinesAsync(meu8File);

    var aesKey = "";
    for (int i = 0; i < meu8FileContent.Length; i++)
    {
        var currentLine = meu8FileContent[i].Replace(" ", "");
        // 处理加密信息
        if (currentLine.StartsWith(M3U8Protocol.EXTM3U_KEY))
        {
            // 不加密
            if (currentLine.Contains("METHOD=NONE"))
            {
                aesKey = "";
            }
            // AES-128加密
            else if (currentLine.Contains("METHOD=AES-128"))
            {
                if (currentLine.Contains("URI="))
                {
                    string keyFileUrl = currentLine.Substring(currentLine.IndexOf("URI=") + 5);
                    keyFileUrl = keyFileUrl.Substring(0, keyFileUrl.Length - 1);
                    // 下载key文件
                    var keyFileBytes = await (new HttpClient()).GetByteArrayAsync($"{host}{keyFileUrl}");
                    var keyFilePath = Path.Combine(curPath, "key.key");
                    File.WriteAllBytes(keyFilePath, keyFileBytes);
                    aesKey = File.ReadAllText(keyFilePath);
                    // 移除
                    File.Delete(keyFilePath);
                }
            }
        }

        // 处理分段信息
        if (currentLine.StartsWith(M3U8Protocol.EXTM3U_EXTINF))
        {
            var _filePath = meu8FileContent[i + 1];
            downloadFiles.Add(new DownloadFile
            {
                AesIv = new byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                AesKey = aesKey,
                FilePath = _filePath.StartsWith("http") ? _filePath : $"{host}{_filePath}"
            });
        }
    }

    int index = 1;
    downloadFiles.ForEach(x =>
    {
        x.FileName = index.ToString() + ".ts";
        x.Order = index;
        index++;
    });

    var taskNumber = 64;

    // 开始多线程下载文件

    var tmpPath = Path.Combine(curPath, "tmp");
    Directory.CreateDirectory(tmpPath);

    ConcurrentQueue<DownloadFile> queue = new ConcurrentQueue<DownloadFile>(downloadFiles);

    Task[] tasks = new Task[taskNumber];

    for (int i = 0; i < taskNumber; i++)
    {
        tasks[i] = Task.Run(async () =>
        {
            while (queue.TryDequeue(out DownloadFile t))
            {
                Console.WriteLine($"下载：{t.FilePath}");
                byte[] tsFileBytes = null;
                try
                {
                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(30));

                    // 下载ts文件
                    tsFileBytes = await (new HttpClient().GetByteArrayAsync($"{t.FilePath}", cts.Token));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"下载：{t.FilePath}失败！");
                }
                if (tsFileBytes != null)
                {
                    var tsFilePath = Path.Combine(tmpPath, t.FileName);
                    // 存在加密
                    if (!string.IsNullOrWhiteSpace(t.AesKey))
                    {
                        var key = Encoding.UTF8.GetBytes(t.AesKey);
                        var iv = Encoding.UTF8.GetBytes("0000000000000000");
                        using Aes myaes = Aes.Create();
                        File.WriteAllBytes(tsFilePath, DecryptStringFromBytes_Aes(tsFileBytes, key, iv));
                    }
                    else
                    {
                        File.WriteAllBytes(tsFilePath, tsFileBytes);
                    }
                }
            }
        });
    }

    Task.WaitAll(tasks);


    // 合并ts文件
    var finalTsFile = Path.Combine(curPath, "final.ts");

    // 重新格式化ts文件并排序
    var tsFiles = Directory.EnumerateFiles(tmpPath, "*.ts").ToList();
    List<TsFile> tsFileList = new List<TsFile>();
    tsFiles.ForEach(x =>
    {
        var name = Path.GetFileNameWithoutExtension(x);
        tsFileList.Add(new TsFile
        {
            FileName = x,
            Order = Convert.ToInt32(name)
        });
    });
    tsFileList = tsFileList.OrderBy(x => x.Order).ToList();

    // 开始合并
    List<byte> finalBytes = new List<byte>();
    tsFileList.ForEach(x =>
    {
        var xx = File.ReadAllBytes(x.FileName);
        finalBytes.AddRange(xx);
    });
    File.WriteAllBytes(finalTsFile, finalBytes.ToArray());

    // 删除tmp文件夹
    Directory.Delete(tmpPath, true);
}



static byte[] DecryptStringFromBytes_Aes(byte[] cipherText, byte[] Key, byte[] IV)
{
    // Check arguments.
    if (cipherText == null || cipherText.Length <= 0)
        throw new ArgumentNullException("cipherText");
    if (Key == null || Key.Length <= 0)
        throw new ArgumentNullException("Key");
    if (IV == null || IV.Length <= 0)
        throw new ArgumentNullException("IV");
    using (Aes aesAlg = Aes.Create())
    {
        aesAlg.Key = Key;
        aesAlg.IV = IV;

        aesAlg.Mode = CipherMode.CBC;
        aesAlg.Padding = PaddingMode.None;
        ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

        int blockSize = decryptor.OutputBlockSize;
        byte[] dataBytes = cipherText;
        int plaintextLength = dataBytes.Length;
        if (plaintextLength % blockSize != 0)
        {
            plaintextLength = plaintextLength + (blockSize - (plaintextLength % blockSize));
        }
        byte[] plaintext = new byte[plaintextLength];
        Array.Copy(dataBytes, 0, plaintext, 0, dataBytes.Length);
        var final = decryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        return final;

    }
}


public class TsFile
{
    public int Order { get; set; }
    public string FileName { get; set; }
}



public class DownloadFile
{
    /// <summary>
    /// 分段排序
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// 分段名称
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// 下载地址
    /// </summary>
    public string FilePath { get; set; }

    /// <summary>
    /// Aes加密IV
    /// </summary>
    public byte[] AesIv { get; set; }

    /// <summary>
    /// Aes加密KEY
    /// </summary>
    public string AesKey { get; set; }

}


public class M3U8Protocol
{
    /// <summary>
    /// m3u8文件标识
    /// </summary>
    public static string EXTM3U = "#EXTM3U";

    /// <summary>
    /// 多个媒体流
    /// </summary>
    public static string EXTM3U_STREAM_INF = "#EXT-X-STREAM-INF";

    /// <summary>
    /// m3u8文件版本号 
    /// </summary>
    public static string EXTM3U_VERSION = "#EXT-X-VERSION";

    /// <summary>
    /// m3u8文件最大时长
    /// </summary>
    public static string EXTM3U_TARGETDURATION = "#EXT-X-TARGETDURATION";

    /// <summary>
    /// m3u8文件加密方式
    /// </summary>
    public static string EXTM3U_KEY = "#EXT-X-KEY";

    /// <summary>
    /// 片段标识
    /// </summary>
    public static string EXTM3U_EXTINF = "#EXTINF";

    /// <summary>
    /// 分段标识
    /// </summary>
    public static string EXTM3U_DISCONTINUITY = "#EXT-X-DISCONTINUITY";


    /// <summary>
    /// 结束标识
    /// </summary>
    public static string EXTM3U_ENDLIST = "#EXT-X-ENDLIST";
}