﻿//
// OpenCover - S Wilde
//
// This source code is released under the MIT License; see the accompanying license file.
//
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCover.Framework.Communication;
using OpenCover.Framework.Model;
using OpenCover.Framework.Persistance;

namespace OpenCover.Framework.Manager
{
    public class ProfilerManager : IProfilerManager
    {
        const int maxMsgSize = 65536;

        private readonly IMessageHandler _messageHandler;
        private readonly IPersistance _persistance;
        private MemoryMappedViewStream _streamAccessorComms;
        private MemoryMappedViewStream _streamAccessorResults;
        private EventWaitHandle _requestDataReady;
        private EventWaitHandle _responseDataReady;
        private EventWaitHandle _requestResultsReady;
        private EventWaitHandle _responseResultsReady;
        private byte[] _dataCommunication;
        private new ConcurrentQueue<byte[]> _messageQueue;

        public ProfilerManager(IMessageHandler messageHandler, IPersistance persistance)
        {
            _messageHandler = messageHandler;
            _persistance = persistance;
        }

        public void RunProcess(Action<Action<StringDictionary>> process)
        {
            var key = Guid.NewGuid().GetHashCode().ToString("X");
            var processMgmt = new AutoResetEvent(false);
            var queueMgmt = new AutoResetEvent(false);
            var environmentKeyRead = new AutoResetEvent(false);
            var handles = new List<WaitHandle> { processMgmt };

            _requestDataReady = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\OpenCover_Profiler_Communication_SendData_Event_" + key);
            _responseDataReady = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\OpenCover_Profiler_Communication_ReceiveData_Event_" + key);

            handles.Add(_requestDataReady);

            _requestResultsReady = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\OpenCover_Profiler_Communication_SendResults_Event_" + key);
            _responseResultsReady = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\OpenCover_Profiler_Communication_ReceiveResults_Event_" + key);

            handles.Add(_requestResultsReady);

            _messageQueue = new ConcurrentQueue<byte[]>();

            using (var mmfComms = MemoryMappedFile.CreateNew(@"Local\OpenCover_Profiler_Communication_MemoryMapFile_" + key, maxMsgSize))
            using (var mmfResults = MemoryMappedFile.CreateNew(@"Local\OpenCover_Profiler_Results_MemoryMapFile_" + key, maxMsgSize))
            using (_streamAccessorComms = mmfComms.CreateViewStream(0, maxMsgSize, MemoryMappedFileAccess.ReadWrite))
            using (_streamAccessorResults = mmfResults.CreateViewStream(0, maxMsgSize, MemoryMappedFileAccess.ReadWrite))
            {
                _streamAccessorResults.Write(BitConverter.GetBytes(0), 0, 4);
                ThreadPool.QueueUserWorkItem((state) =>
                {
                    try
                    {
                        process(dictionary =>
                        {
                            if (dictionary == null) return;
                            dictionary[@"OpenCover_Profiler_Key"] = key;
                            dictionary["Cor_Profiler"] = "{1542C21D-80C3-45E6-A56C-A9C1E4BEB7B8}";
                            dictionary["Cor_Enable_Profiling"] = "1";
                            environmentKeyRead.Set();
                        });
                    }
                    finally
                    {
                        processMgmt.Set();
                    }
                });

                ThreadPool.QueueUserWorkItem((state) =>
                {
                    while (true)
                    {
                        byte[] data;
                        if (_messageQueue.TryDequeue(out data))
                        {
                            if (data.Length == 0)
                            {
                                _messageHandler.Complete();
                                queueMgmt.Set();
                                return;
                            }
                            
                            _persistance.SaveVisitData(data);
                        }
                        else
                        {
                            Thread.Yield();
                        } 
                    }
                });

                // wait for the environment key to be read
                if (WaitHandle.WaitAny(new[] { environmentKeyRead }, new TimeSpan(0, 0, 0, 10)) == -1) 
                    return;

                _dataCommunication = new byte[maxMsgSize];
                var pinnedComms = GCHandle.Alloc(_dataCommunication, GCHandleType.Pinned);
                try
                {
                    ProcessMessages(handles, pinnedComms);
                }
                finally
                {
                    pinnedComms.Free();
                }

                queueMgmt.WaitOne();
            }
        }

        private void ProcessMessages(List<WaitHandle> handles, GCHandle pinnedComms)
        {
            byte[] data = null;
            var continueWait = true;
            do
            {
                if (data == null) data = new byte[maxMsgSize];
                switch (WaitHandle.WaitAny(handles.ToArray()))
                {
                    case 1:
                        _requestDataReady.Reset();
                                            
                        _streamAccessorComms.Seek(0, SeekOrigin.Begin);
                        _streamAccessorComms.Read(_dataCommunication, 0, _messageHandler.ReadSize);

                        var writeSize = _messageHandler.StandardMessage(
                            (MSG_Type)BitConverter.ToInt32(_dataCommunication, 0), 
                            pinnedComms.AddrOfPinnedObject(), 
                            SendChunkAndWaitForConfirmation);
                            
                        _streamAccessorComms.Seek(0, SeekOrigin.Begin);
                        _streamAccessorComms.Write(_dataCommunication, 0, writeSize);

                        _responseDataReady.Set();
                        _responseDataReady.Reset();

                        break;
                    case 2:                     
                        _requestResultsReady.Reset();

                        _streamAccessorResults.Seek(0, SeekOrigin.Begin);
                        _streamAccessorResults.Read(data, 0, maxMsgSize);

                        _responseResultsReady.Set();
                        _responseResultsReady.Reset();

                        _messageQueue.Enqueue(data);
                        data = null;
                        break;
                    default:
                        continueWait = false;
                        break;
                }
            } while (continueWait);

            _streamAccessorResults.Seek(0, SeekOrigin.Begin);
            _streamAccessorResults.Read(data, 0, maxMsgSize);

            _messageQueue.Enqueue(data);
            _messageQueue.Enqueue(new byte[0]);

        }

        private void SendChunkAndWaitForConfirmation(int writeSize)
        {
            _streamAccessorComms.Seek(0, SeekOrigin.Begin);
            _streamAccessorComms.Write(_dataCommunication, 0, writeSize);
            
            _requestDataReady.Reset();
            WaitHandle.SignalAndWait(_responseDataReady, _requestDataReady);
            _responseDataReady.Reset();
        }
    }
}