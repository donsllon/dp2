﻿// #define SENDKEY

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DigitalPlatform;
using DigitalPlatform.Core;
using DigitalPlatform.RFID;
using DigitalPlatform.Text;

namespace RfidCenter
{
    public class RfidServer : MarshalByRefObject, IRfid, IDisposable
    {
        static CompactLog _compactLog = new CompactLog();

        public void Dispose()
        {
#if SENDKEY
            _cancelInventory?.Cancel();
            _cancelInventory?.Dispose();
#endif
        }

        public GetLockStateResult GetShelfLockState(string lockName,
            string indices)
        {
            List<LockState> states = new List<LockState>();
            string[] list = indices.Split(new char[] { ',' });
            foreach (var one in list)
            {
                // 探测锁状态
                // parameters:
                //      lockName    锁名字。如果为 * 表示所有的锁
                //      index       锁编号。从 0 开始计数
                var result = Program.Rfid.GetShelfLockState(lockName, Convert.ToInt32(one));
                if (result.Value == -1)
                    return result;
                states.AddRange(result.States);
            }

            return new GetLockStateResult { Value = 0, States = states };
        }


        public NormalResult GetState(string style)
        {
            if (style.StartsWith("clearCache"))
            {
                string session_id = StringUtil.GetParameterByPrefix(style, "clearCache");
                if (string.IsNullOrEmpty(session_id))
                    ClearLastUidTable();
                else
                    SetLastUids(session_id, "");
                return new NormalResult();
            }


            if (style == "restart")
            {
                Program.MainForm.Restart();
                return new NormalResult();
            }

            if (Program.MainForm.ErrorState == "normal")
            {
                var result = ListReaders();
                if (result.Readers.Length == 0)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorCode = "noReaders",
                        ErrorInfo = "没有任何连接的读卡器"
                    };
                return new NormalResult
                {
                    Value = 0,
                    ErrorCode = Program.MainForm.ErrorState,
                    ErrorInfo = Program.MainForm.ErrorStateInfo
                };
            }
            return new NormalResult
            {
                Value = -1,
                ErrorCode = Program.MainForm.ErrorState,
                ErrorInfo = Program.MainForm.ErrorStateInfo
            };
        }

        public NormalResult ActivateWindow()
        {
            Program.MainForm.ActivateWindow();
            return new NormalResult();
        }

        // 列出当前可用的 reader
        public ListReadersResult ListReaders()
        {
            // 选出已经成功打开的部分 Reader 返回
            List<string> readers = new List<string>();
            foreach (Reader reader in Program.Rfid.Readers)
            {
                if (reader.Result.Value == 0)
                    readers.Add(reader.Name);
            }
            return new ListReadersResult { Readers = readers.ToArray() };
        }

        // static string _lastUids = "";

        // session_id --> lastUids 对照表
        static Hashtable _lastUidTable = new Hashtable();
        // static object _sync_lastuids = new object();

        // 构造用于比较的 uid 字符串
        static string BuildUids(List<OneTag> tags)
        {
            if (tags == null || tags.Count == 0)
                return "";

            StringBuilder current = new StringBuilder();
            foreach (OneTag tag in tags)
            {
                current.Append($"{tag.UID}|{tag.AntennaID},");
            }
            return current.ToString();
        }

        // 清除 Hashtable
        static void ClearLastUidTable()
        {
            lock (_lastUidTable.SyncRoot)
            {
                _lastUidTable.Clear();
            }
        }

        static void SetLastUids(string session_id, string value)
        {
            /*
            lock (_sync_lastuids)
            {
                _lastUids = value;
            }
            */
            if (session_id == null)
                session_id = "";
            lock (_lastUidTable.SyncRoot)
            {
                // 防止 Hashtable 太大
                if (_lastUidTable.Count > 1000)
                    _lastUidTable.Clear();
                _lastUidTable[session_id] = value;
            }
        }

        static bool CompareLastUids(string session_id, string value)
        {
            /*
            lock (_sync_lastuids)
            {
                if (_lastUids != value)
                    return true;
                return false;
            }
            */
            if (session_id == null)
                session_id = "";
            string lastUids = "";
            lock (_lastUidTable.SyncRoot)
            {
                if (_lastUidTable.ContainsKey(session_id))
                {
                    lastUids = (string)_lastUidTable[session_id];
                }
            }

            if (lastUids != value)
                return true;
            return false;
        }

        // 增加了无标签时延迟等待功能。敏捷响应
        public ListTagsResult ListTags(string reader_name, string style)
        {
            if (Program.Rfid.Pause)
                return new ListTagsResult
                {
                    Value = -1,
                    ErrorInfo = "RFID 功能处于暂停状态",
                    ErrorCode = "paused"
                };

            Program.Rfid.IncApiCount();
            try
            {
                if (Program.Rfid.Pause)
                    return new ListTagsResult
                    {
                        Value = -1,
                        ErrorInfo = "RFID 功能处于暂停状态",
                        ErrorCode = "paused"
                    };

                string session_id = StringUtil.GetParameterByPrefix(style, "session");

                TimeSpan length = TimeSpan.FromSeconds(2);
                ListTagsResult result = null;
                string current_uids = "";
                DateTime start = DateTime.Now;
                while (DateTime.Now - start < length
                    || result == null)
                {
                    result = _listTags(reader_name, style);

                    if (result != null && result.Results != null)
                        current_uids = BuildUids(result.Results);
                    else
                        current_uids = "";

                    // TODO: 这里的比较应该按照 Session 来进行
                    // 只要本次和上次 tag 数不同，立刻就返回
                    if (CompareLastUids(session_id, current_uids))
                    {
                        SetLastUids(session_id, current_uids);
                        return result;
                    }

                    if (result.Value == -1)
                        return result;
                    /*
                    // TODO: 如果本次和上次都是 2，是否立即返回？可否先对比一下 uid，有差别再返回?
                    if (result.Results != null
                        && result.Results.Count > 0)
                    {
                        SetLastUids(current_uids);
                        return result;
                    }
                    */
                    Thread.Sleep(10);
                }

                SetLastUids(session_id, current_uids);
                return result;
            }
            catch (Exception ex)
            {
                return new ListTagsResult
                {
                    Value = -1,
                    ErrorInfo = $"ListTags() 出现异常:{ex.Message}"
                };
            }
            finally
            {
                Program.Rfid.DecApiCount();
            }
        }

        static uint _currenAntenna = 1;
        DateTime _lastTime;

        // parameters:
        //      style   如果为 "getTagInfo"，表示要在结果中返回 TagInfo
        ListTagsResult _listTags(string reader_name, string style)
        {
            InventoryResult result = new InventoryResult();

            if (Program.MainForm.ErrorState != "normal")
                return new ListTagsResult
                {
                    Value = -1,
                    ErrorInfo = $"{Program.MainForm.ErrorStateInfo}",
                    ErrorCode = $"state:{Program.MainForm.ErrorState}"
                };

            List<OneTag> tags = new List<OneTag>();

            // uid --> OneTag
            Hashtable uid_table = new Hashtable();

            foreach (Reader reader in Program.Rfid.Readers)
            {
#if NO
                if (reader_name == "*" || reader.Name == reader_name)
                {

                }
                else
                    continue;
#endif

                if (Reader.MatchReaderName(reader_name, reader.Name) == false)
                    continue;

                InventoryResult inventory_result = Program.Rfid.Inventory(reader.Name,
                    style   // ""
                    );
                if (inventory_result.Value == -1)
                {
                    return new ListTagsResult { Value = -1, ErrorInfo = inventory_result.ErrorInfo, ErrorCode = inventory_result.ErrorCode };
                }

                foreach (InventoryInfo info in inventory_result.Results)
                {
                    OneTag tag = null;
                    if (uid_table.ContainsKey(info.UID))
                    {
                        // 重复出现的，追加 读卡器名字
                        tag = (OneTag)uid_table[info.UID];
                        tag.ReaderName += "," + reader.Name;
                    }
                    else
                    {
                        // 首次出现
                        tag = new OneTag
                        {
                            Protocol = info.Protocol,
                            ReaderName = reader.Name,
                            UID = info.UID,
                            DSFID = info.DsfID,
                            AntennaID = info.AntennaID, // 2019/9/25
                            // InventoryInfo = info    // 有些冗余的字段
                        };

                        /*
                        // testing
                        tag.AntennaID = _currenAntenna;
                        if (DateTime.Now - _lastTime > TimeSpan.FromSeconds(5))
                        {
                            _currenAntenna++;
                            if (_currenAntenna > 50)
                                _currenAntenna = 1;
                            _lastTime = DateTime.Now;
                        }
                        */

                        uid_table[info.UID] = tag;
                        tags.Add(tag);
                    }

                    if (StringUtil.IsInList("getTagInfo", style)
                        && tag.TagInfo == null)
                    {
                        // TODO: 这里要利用 Hashtable 缓存
                        GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader.Name, info);
                        if (result0.Value == -1)
                        {
                            tag.TagInfo = null;
                            // TODO: 如何报错？写入操作历史?
                            // $"读取标签{info.UID}信息时出错:{result0.ToString()}"
                        }
                        else
                        {
                            tag.TagInfo = result0.TagInfo;
                        }
                    }
#if NO
                            GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader.Name, info);
                            if (result0.Value == -1)
                            {
                                // TODO: 如何报错？写入操作历史?
                                Program.MainForm.OutputText($"读取标签{info.UID}信息时出错:{result0.ToString()}", 2);
                                continue;
                            }

                            LogicChip chip = LogicChip.From(result0.TagInfo.Bytes,
                                (int)result0.TagInfo.BlockSize,
                                "" // result0.TagInfo.LockStatus
                                );
                            Element pii = chip.FindElement(ElementOID.PII);
                            if (pii == null)
                            {
                                Program.MainForm.Invoke((Action)(() =>
                                {
                                    // 发送 UID
                                    SendKeys.SendWait($"uid:{info.UID}\r");
                                }));
                            }
                            else
                            {
                                Program.MainForm.Invoke((Action)(() =>
                                {
                                    // 发送 PII
                                    SendKeys.SendWait($"pii:{pii.Text}\r");
                                }));
                            }
#endif
                }
            }

            return new ListTagsResult { Results = tags };

#if NO
            InventoryResult result = new InventoryResult();
            List<OneTag> tags = new List<OneTag>();
            _lockTagList.EnterReadLock();
            try
            {
                foreach (OneTag tag in _tagList)
                {
                    if (reader_name == "*" || tag.ReaderName == reader_name)
                        tags.Add(tag);
                }
                return new ListTagsResult { Results = tags };
            }
            finally
            {
                _lockTagList.ExitReadLock();
            }
#endif
        }

        // 2019/9/25
        // 新版本。根据 InventoryInfo 获得标签详细信息
        // result.Value
        //      -1
        //      0
        public GetTagInfoResult GetTagInfo(string reader_name,
            InventoryInfo info)
        {
            if (Program.MainForm.ErrorState != "normal")
                return new GetTagInfoResult
                {
                    Value = -1,
                    ErrorInfo = $"{Program.MainForm.ErrorStateInfo}",
                    ErrorCode = $"state:{Program.MainForm.ErrorState}"
                };

            List<GetTagInfoResult> errors = new List<GetTagInfoResult>();
            foreach (Reader reader in Program.Rfid.Readers)
            {
                if (Reader.MatchReaderName(reader_name, reader.Name) == false)
                    continue;

                // result.Value
                //      -1
                //      0
                GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader.Name, info);

                // 继续尝试往后寻找
                if (result0.Value == -1
                    // && result0.ErrorCode == "errorFromReader=4"
                    )
                {
                    errors.Add(result0);
                    continue;
                }

                if (result0.Value == -1)
                    return result0;

                // found
                return result0;
            }

            if (errors.Count > 0)
                return errors[0];

            return new GetTagInfoResult { ErrorCode = "notFoundReader" };
        }

        // 2019/9/27 增加的 antenna_id
        // result.Value
        //      -1
        //      0
        public GetTagInfoResult GetTagInfo(string reader_name,
            string uid,
            uint antenna_id)
        {
            if (Program.MainForm.ErrorState != "normal")
                return new GetTagInfoResult
                {
                    Value = -1,
                    ErrorInfo = $"{Program.MainForm.ErrorStateInfo}",
                    ErrorCode = $"state:{Program.MainForm.ErrorState}"
                };

            List<GetTagInfoResult> errors = new List<GetTagInfoResult>();
            foreach (Reader reader in Program.Rfid.Readers)
            {
#if NO
                if (reader_name == "*" || reader.Name == reader_name)
                {

                }
                else
                    continue;
#endif
                if (Reader.MatchReaderName(reader_name, reader.Name) == false)
                    continue;

                InventoryInfo info = new InventoryInfo
                {
                    UID = uid,
                    AntennaID = antenna_id
                };

                // result.Value
                //      -1
                //      0
                GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader.Name, info);

                // 继续尝试往后寻找
                if (result0.Value == -1
                    // && result0.ErrorCode == "errorFromReader=4"
                    )
                {
                    errors.Add(result0);
                    continue;
                }

                if (result0.Value == -1)
                    return result0;

                // found
                return result0;
            }

            // 2019/2/13
            if (errors.Count > 0)
                return errors[0];

            return new GetTagInfoResult { ErrorCode = "notFoundReader" };
        }

        public NormalResult WriteTagInfo(
    string reader_name,
    TagInfo old_tag_info,
    TagInfo new_tag_info)
        {
            // TODO: 对 old_tag_info 和 new_tag_info 合法性进行一系列检查

            foreach (Reader reader in Program.Rfid.Readers)
            {
#if NO
                if (reader_name == "*" || reader.Name == reader_name)
                {

                }
                else
                    continue;
#endif

                if (Reader.MatchReaderName(reader_name, reader.Name) == false)
                    continue;


                InventoryInfo info = new InventoryInfo
                {
                    UID = old_tag_info.UID,
                    AntennaID = old_tag_info.AntennaID  // 2019/9/27
                };
                GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader.Name, info);

                if (result0.Value == -1 && result0.ErrorCode == "errorFromReader=4")
                    continue;

                if (result0.Value == -1)
                    return new NormalResult(result0);

                // TODO: 是否对照检查 old_tag_info 和 result0.TagInfo ?

                return Program.Rfid.WriteTagInfo(reader.Name,
                    old_tag_info,
                    new_tag_info);
            }

            return new NormalResult
            {
                Value = -1,
                ErrorInfo = $"没有找到 UID 为 {old_tag_info.UID} 的标签",
                ErrorCode = "notFound"
            };
        }

        /*
        // 兼容以前的 API
        public NormalResult SetEAS(
string reader_name,
string tag_name,
bool enable)
        {
            return SetEAS(reader_name, tag_name, 0, enable);
        }
        */

        // parameters:
        //      reader_name 读卡器名字。也可以为 "*"，表示所有读卡器
        //      tag_name    标签名字。为 pii:xxxx 或者 uid:xxxx 形态。若没有冒号，则默认为是 UID
        //      style   如果标签所在的天线不是 1 号天线，要用 antenna:1|2|3|4 来进行调用(列出一个或者多个可能的天线号)
        // return result.Value:
        //      -1  出错
        //      0   没有找到指定的标签
        //      1   找到，并成功修改 EAS
        public NormalResult SetEAS(
string reader_name,
string tag_name,
uint antenna_id,
bool enable)
        {
            string uid = "";
            List<string> parts = StringUtil.ParseTwoPart(tag_name, ":");
            if (parts[0] == "pii")
            {
                // 2019/9/24
                // 天线列表
                // 1|2|3|4 这样的形态

                FindTagResult result = Program.Rfid.FindTagByPII(
                    reader_name,
                    InventoryInfo.ISO15693, // 只有 ISO15693 才有 EAS (2019/8/28)
                    antenna_id.ToString(),
                    parts[1]);
                if (result.Value != 1)
                    return new NormalResult
                    {
                        Value = result.Value,
                        ErrorInfo = result.ErrorInfo,
                        ErrorCode = result.ErrorCode
                    };
                uid = result.UID;
                reader_name = result.ReaderName;    // 假如最初 reader_name 为 '*'，此处可以改为具体的读卡器名字，会加快后面设置的速度
            }
            else if (parts[0] == "uid" || string.IsNullOrEmpty(parts[0]))
                uid = parts[1];
            else
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"未知的 tag_name 前缀 '{parts[0]}'",
                    ErrorCode = "unknownPrefix"
                };

            {
                // TODO: 检查 uid 字符串内容是否合法。应为 hex 数字

                // return result.Value
                //      -1  出错
                //      0   成功
                NormalResult result = Program.Rfid.SetEAS(
reader_name,
uid,
antenna_id,
enable);
                if (result.Value == -1)
                    return result;
                return new NormalResult { Value = 1 };
            }
        }

        public NormalResult ChangePassword(string reader_name,
    string uid,
    string type,
    uint old_password,
    uint new_password)
        {
            return Program.Rfid.ChangePassword(
reader_name,
uid,
type,
old_password,
new_password);
        }

#if SENDKEY

        #region Tag List

        // 当前在读卡器探测范围内的标签
        static List<OneTag> _tagList = new List<OneTag>();
        static internal ReaderWriterLockSlim _lockTagList = new ReaderWriterLockSlim();

        bool AddToTagList(string reader_name,
            string uid,
            byte dsfid,
            string protocol)
        {
            OneTag tag = FindTag(uid);
            if (tag != null)
                return false;
            _lockTagList.EnterWriteLock();
            try
            {
                tag = new OneTag
                {
                    Protocol = protocol,
                    ReaderName = reader_name,
                    UID = uid,
                    LastActive = DateTime.Now,
                    DSFID = dsfid
                };
                tag.LastActive = DateTime.Now;
                _tagList.Add(tag);
            }
            finally
            {
                _lockTagList.ExitWriteLock();
            }

            // 触发通知动作
            // TODO: 通知以后，最好把标签内容信息给存储起来，这样 Inventory 的时候可以直接使用
            if (_sendKeyEnabled.Value == true)
                Notify(tag.ReaderName, tag.UID, tag.Protocol);
            return true;
        }

        OneTag FindTag(string uid)
        {
            _lockTagList.EnterReadLock();
            try
            {
                foreach (OneTag tag in _tagList)
                {
                    if (tag.UID == uid)
                    {
                        tag.LastActive = DateTime.Now;
                        return tag;
                    }
                }
                return null;
            }
            finally
            {
                _lockTagList.ExitReadLock();
            }
        }

        void ClearIdleTag(TimeSpan delta)
        {
            List<OneTag> delete_tags = new List<OneTag>();
            _lockTagList.EnterReadLock();
            try
            {
                DateTime now = DateTime.Now;
                foreach (OneTag tag in _tagList)
                {
                    if (now - tag.LastActive >= delta)
                        delete_tags.Add(tag);
                }
            }
            finally
            {
                _lockTagList.ExitReadLock();
            }

            if (delete_tags.Count > 0)
            {
                _lockTagList.EnterWriteLock();
                try
                {
                    foreach (OneTag tag in delete_tags)
                    {
                        _tagList.Remove(tag);
                    }
                }
                finally
                {
                    _lockTagList.ExitWriteLock();
                }
            }
        }

        void Notify(string reader_name, string uid, string protocol)
        {
            Task.Run(() =>
            {
                bool succeed = false;
                for (int i = 0; i < 10; i++)
                {
                    succeed = NotifyTag(reader_name, uid, protocol);
                    if (succeed == true)
                        break;
                    Thread.Sleep(100);
                }
                if (succeed == false)
                    Program.MainForm.OutputHistory($"读卡器{reader_name}读取标签{uid}详细信息时出错", 1);
            });
        }

        #endregion

#endif

        static private AtomicBoolean _sendKeyEnabled = new AtomicBoolean(false);

        public NormalResult EnableSendKey(bool enable)
        {
            // 如果和以前的值相同
            bool old_value = _sendKeyEnabled.Value;
            if (old_value == enable)
                return new NormalResult();

            if (enable == true)
                _sendKeyEnabled.FalseToTrue();
            else
                _sendKeyEnabled.TrueToFalse();

            string message = "";
            if (enable)
                message = "RFID 发送打开";
            else
                message = "RFID 发送关闭";

            Task.Run(() =>
            {
                Program.MainForm?.OutputHistory(message, 0);
                Program.MainForm?.Speak(message);
            });

            return new NormalResult();
        }

        // 开始或者结束捕获标签
        public NormalResult BeginCapture(bool begin)
        {
#if SENDKEY
            StartInventory(begin);
#endif
            return new NormalResult();
        }

#if SENDKEY
        // 启动或者停止自动盘点
        void StartInventory(bool start)
        {
            // TODO: 是否要加锁，让本函数不能并行执行？
            if (start)
            {
                _cancelInventory?.Cancel();
                while (_cancelInventory != null)
                {
                    Task.Delay(500).Wait();
                }

                var task = DoInventory();
                // Task.Run(() => { DoInventory(); });
            }
            else
            {
                _cancelInventory?.Cancel();
                while (_cancelInventory != null)
                {
                    Task.Delay(500).Wait();
                }
            }
        }

        static CancellationTokenSource _cancelInventory = null;

        async Task DoInventory()
        {
            Program.MainForm.OutputHistory("开始捕获", 0);

            /*
            if (Program.Rfid.Readers.Count == 0)
                Program.MainForm.OutputHistory("当前没有可用的读卡器", 2);
            else
            {
                List<string> names = new List<string>();
                Program.Rfid.Readers.ForEach((o) => names.Add(o.Name));
                Program.MainForm.OutputHistory($"当前读卡器数量 {Program.Rfid.Readers.Count}。包括: \r\n{StringUtil.MakePathList(names, "\r\n")}", 0);
            }
            */

            if (Program.Rfid.ShelfLocks.Count > 0)
            {
                List<string> names = new List<string>();
                Program.Rfid.ShelfLocks.ForEach((o) => names.Add(o.Name));
                Program.MainForm.OutputHistory($"当前锁控数量 {Program.Rfid.ShelfLocks.Count}。包括: \r\n{StringUtil.MakePathList(names, "\r\n")}", 0);
            }

            _cancelInventory = new CancellationTokenSource();
            bool bFirst = true;
            try
            {
                // uid --> Driver Name
                // Hashtable uid_table = new Hashtable();
                while (_cancelInventory.IsCancellationRequested == false)
                {
                    await Task.Delay(200, _cancelInventory.Token).ConfigureAwait(false);

                    ClearIdleTag(TimeSpan.FromSeconds(1));  // 1 秒的防误触发时间

                    FlushCompactLog();

                    //if (_captureEnabled.Value == false)
                    //    continue;

                    // uid_table.Clear();
                    foreach (Reader reader in Program.Rfid.Readers)
                    {
                        if (reader == null)
                            continue;

                        if (Program.Rfid.Pause)
                            continue;

                        if (string.IsNullOrEmpty(Program.Rfid.State) == false)
                            break;

                        InventoryResult inventory_result = null;
                        //Program.Rfid.IncApiCount();
                        try
                        {
                            inventory_result = Program.Rfid.Inventory(
      reader.Name, bFirst ? "" : "only_new");
                            // bFirst = false;
                        }
                        finally
                        {
                            //Program.Rfid.DecApiCount();
                        }

                        if (inventory_result.Value == -1)
                        {
                            _compactLog?.Add("*** 读卡器 {0} 点选标签时出错: {1}",
                                new object[] { reader.Name, inventory_result.ToString() }
                                );
                            continue;
                            // ioError 要主动卸载有问题的 reader?
                            // 如何报错？写入操作历史？
                            // Program.MainForm.OutputHistory($"读卡器{reader.Name}点选标签时出错:{inventory_result.ToString()}\r\n已停止捕获过程", 2);
                            // return;
                        }

                        foreach (InventoryInfo info in inventory_result.Results)
                        {
                            //if (uid_table.ContainsKey(info.UID))
                            //    continue;
                            //uid_table[info.UID] = reader.Name;
                            AddToTagList(reader.Name, info.UID, info.DsfID, info.Protocol);
                        }

                    }
                }
            }
            catch (TaskCanceledException)
            {

            }
            finally
            {
                _cancelInventory = null;
                Program.MainForm.OutputHistory("结束捕获", 0);
            }
        }


        static DateTime _lastFlushTime = DateTime.Now;
        static int _lastErrorCount = 0;

        void FlushCompactLog()
        {
            if (_compactLog == null)
                return;

            int minutes = 10;    // 分钟数
            TimeSpan delta = TimeSpan.FromMinutes(minutes);   // 10

            if (DateTime.Now - _lastFlushTime > delta)
            {
                _lastErrorCount += _compactLog.WriteToLog((text) =>
                {
                    Program.MainForm.OutputHistory(text, 2);
                });
                _lastFlushTime = DateTime.Now;

                if (_lastErrorCount > 200 * minutes)  // 200 相当于一分钟连续报错的量
                {
                    // 触发重启全部读卡器
                    Program.MainForm?.BeginRefreshReaders("connected", new CancellationToken());
                    Program.MainForm?.Speak("尝试重新初始化全部读卡器");
                    _lastErrorCount = 0;
                }
            }
        }

        bool NotifyTag(string reader_name, string uid, string protocol)
        {
            if (_sendKeyEnabled.Value == false)
                return false;

            // 2019/2/24
            if (protocol == InventoryInfo.ISO14443A)
            {
                Program.MainForm.Invoke((Action)(() =>
                {
                    // 发送 UID
                    SendKeys.SendWait($"uid:{uid},tou:80\r");
                }));
                Program.MainForm?.Speak("发送");
                return true;
            }

            InventoryInfo info = new InventoryInfo { UID = uid };
            GetTagInfoResult result0 = Program.Rfid.GetTagInfo(reader_name, info);
            if (result0.Value == -1)
            {
                // TODO: 如何报错？写入操作历史?
                // Program.MainForm.OutputText($"读取标签{info.UID}信息时出错:{result0.ToString()}", 2);
                return false;
            }

            LogicChip chip = LogicChip.From(result0.TagInfo.Bytes,
                (int)result0.TagInfo.BlockSize,
                "" // result0.TagInfo.LockStatus
                );
            Element pii = chip.FindElement(ElementOID.PII);
            Element typeOfUsage = chip.FindElement(ElementOID.TypeOfUsage);

            StringBuilder text = new StringBuilder();
            if (pii == null)
                text.Append($"uid:{info.UID}");
            else
                text.Append($"pii:{pii.Text}");
            if (typeOfUsage != null)
                text.Append($",tou:{typeOfUsage.Text}");

            Program.MainForm.Invoke((Action)(() =>
            {
                // 发送 UID
                SendKeys.SendWait($"{text}\r");
            }));
            Program.MainForm?.Speak("发送");

            return true;
        }

#endif

    }
}
