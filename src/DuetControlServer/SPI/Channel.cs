﻿using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.FileExecution;
using DuetControlServer.SPI.Communication;
using Nito.AsyncEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Class used to hold internal information about a single code channel
    /// </summary>
    public class ChannelInformation
    {
        /// <summary>
        /// Constructor of a code channel descriptor
        /// </summary>
        /// <param name="channel">Code channel of this instance</param>
        public ChannelInformation(CodeChannel channel)
        {
            Channel = channel;
        }

        /// <summary>
        /// What code channel this class is about
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Indicates if this channel is blocked until the next full transfer
        /// </summary>
        public bool IsBlocked { get; set; }

        /// <summary>
        /// Lock used when accessing this instance
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Prioritised codes that override every other code
        /// </summary>
        public readonly Queue<QueuedCode> PriorityCodes = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending G/M/T-codes that have not been buffered yet
        /// </summary>
        public readonly Queue<QueuedCode> PendingCodes = new Queue<QueuedCode>();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; set; }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public readonly List<QueuedCode> BufferedCodes = new List<QueuedCode>();

        /// <summary>
        /// Stack of suspended G/M/T-codes to resend when the current macro file finishes
        /// </summary>
        public readonly Stack<Queue<QueuedCode>> SuspendedCodes = new Stack<Queue<QueuedCode>>();

        /// <summary>
        /// Indicates whether the requested system macro file has finished
        /// </summary>
        public bool SystemMacroHasFinished = false;

        /// <summary>
        /// Stack of nested macro files being executed
        /// </summary>
        public readonly Stack<MacroFile> NestedMacros = new Stack<MacroFile>();

        /// <summary>
        /// Pending codes being started by a nested macro (and multiple codes may be started by an interceptor).
        /// This is required because it may take a moment until they are internally processed
        /// </summary>
        public readonly Queue<QueuedCode> NestedMacroCodes = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending lock/unlock requests
        /// </summary>
        public readonly Queue<QueuedLockRequest> PendingLockRequests = new Queue<QueuedLockRequest>();

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public readonly Queue<TaskCompletionSource<bool>> PendingFlushRequests = new Queue<TaskCompletionSource<bool>>();

        /// <summary>
        /// Indicates if this channel is blocked and waiting for a message acknowledgement
        /// </summary>
        public bool WaitingForMessageAcknowledgement { get; set; }

        /// <summary>
        /// Lock access to this code channel
        /// </summary>
        /// <returns>Disposable lock</returns>
        public IDisposable Lock() => _lock.Lock();

        /// <summary>
        /// Lock access to this code channel asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync();

        /// <summary>
        /// Write channel diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        public void Diagnostics(StringBuilder builder)
        {
            StringBuilder channelDiagostics = new StringBuilder();

            foreach (QueuedCode pendingCode in PendingCodes)
            {
                channelDiagostics.AppendLine($"Pending code: {pendingCode.Code}");
            }

            foreach (QueuedCode bufferedCode in BufferedCodes)
            {
                channelDiagostics.AppendLine($"Buffered code: {bufferedCode.Code}");
            }
            if (BytesBuffered != 0)
            {
                channelDiagostics.AppendLine($"=> {BytesBuffered} bytes");
            }

            foreach (Queue<QueuedCode> suspendedCodes in SuspendedCodes)
            {
                channelDiagostics.AppendLine("> Suspended code level");
                foreach (QueuedCode suspendedCode in suspendedCodes)
                {
                    channelDiagostics.AppendLine($"Suspended code: {suspendedCode.Code}");
                }
            }

            foreach (MacroFile macroFile in NestedMacros)
            {
                channelDiagostics.AppendLine($"Nested macro: {macroFile.FileName}, started by: {((macroFile.StartCode == null) ? "system" : macroFile.StartCode.ToString())}");
            }

            foreach (QueuedCode nestedMacroCode in NestedMacroCodes)
            {
                channelDiagostics.AppendLine($"Nested macro code: {nestedMacroCode.Code}");
            }

            if (PendingLockRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"Number of lock/unlock requests: {PendingLockRequests.Count(item => item.IsLockRequest)}/{PendingLockRequests.Count(item => !item.IsLockRequest)}");
            }

            if (PendingFlushRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"Number of flush requests: {PendingFlushRequests.Count}");
            }

            if (WaitingForMessageAcknowledgement)
            {
                channelDiagostics.AppendLine("Waiting for message acknowledgment");
            }

            if (channelDiagostics.Length != 0)
            {
                builder.AppendLine($"{Channel}:");
                builder.Append(channelDiagostics);
            }
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        /// <returns>If anything more can be done on this channel</returns>
        public bool ProcessRequests()
        {
            // 1. Priority codes
            if (PriorityCodes.TryPeek(out QueuedCode queuedCode))
            {
                if (queuedCode.IsFinished || (queuedCode.IsReadyToSend && BufferCode(queuedCode)))
                {
                    PriorityCodes.Dequeue();
                    return true;
                }

                // Block this channel until every priority code is gone
                IsBlocked = true;
            }
                
            // 2. Suspended codes being resumed (may include suspended codes from nested macros)
            if (_resumingBuffer)
            {
                ResumeBuffer();
                return _resumingBuffer;
            }

            // FIXME This doesn't work yet for non-M292 codes. Needs more refactoring
            if (WaitingForMessageAcknowledgement)
            {
                // Still waiting for M292...
                return false;
            }

            // 3. Macro codes
            if (NestedMacroCodes.TryPeek(out queuedCode) && (queuedCode.IsFinished || (queuedCode.IsReadyToSend && BufferCode(queuedCode))))
            {
                NestedMacroCodes.Dequeue();
                return true;
            }

            // 4. New codes from macro files
            if (NestedMacros.TryPeek(out MacroFile macroFile))
            {
                // Try to read the next real code from the system macro being executed
                Commands.Code code = null;
                if (!macroFile.IsFinished && NestedMacroCodes.Count < Settings.BufferedMacroCodes)
                {
                    code = macroFile.ReadCode();
                }

                // If there is any, start executing it in the background. An interceptor may also generate extra codes
                if (code != null)
                {
                    // Note that the following code is executed asynchronously to avoid potential
                    // deadlocks which would occur when SPI data is awaited (e.g. heightmap queries)
                    queuedCode = new QueuedCode(code);
                    NestedMacroCodes.Enqueue(queuedCode);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            CodeResult result = await code.Execute();
                            if (!queuedCode.IsReadyToSend)
                            {
                                // Macro codes need special treatment because they may complete before they are actually sent to RepRapFirmware
                                queuedCode.HandleReply(result);
                            }
                            if (!macroFile.IsAborted)
                            {
                                await Utility.Logger.LogOutput(result);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Something has gone wrong and the SPI connector has invalidated everything - don't deal with this (yet?)
                        }
                        catch (AggregateException ae)
                        {
                            // FIXME: Should this terminate the macro being executed?
                            Console.WriteLine($"[err] {code} -> {ae.InnerException.Message}");
                        }
                    });

                    return true;
                }

                // Macro file is complete if no more codes can be read from the file and the buffered codes are completely gone
                if (macroFile.IsFinished && !NestedMacroCodes.TryPeek(out _) && BufferedCodes.Count == 0 &&
                    ((macroFile.StartCode != null && macroFile.StartCode.DoingNestedMacro) || (macroFile.StartCode == null && !SystemMacroHasFinished)) &&
                    MacroCompleted(macroFile.StartCode, macroFile.IsAborted))
                {
                    if (macroFile.StartCode == null)
                    {
                        SystemMacroHasFinished = true;
                    }
                    Console.WriteLine($"[info] {(macroFile.IsAborted ? "Aborted" : "Finished")} macro file '{Path.GetFileName(macroFile.FileName)}'");
                    return false;
                }
            }

            // 5. Regular codes - only applicable if no macro is being executed
            else if (PendingCodes.TryPeek(out queuedCode) && BufferCode(queuedCode))
            {
                PendingCodes.Dequeue();
                return true;
            }

            // 6. Lock/Unlock requests
            if (BufferedCodes.Count == 0 && PendingLockRequests.TryPeek(out QueuedLockRequest lockRequest))
            {
                if (lockRequest.IsLockRequest)
                {
                    if (!lockRequest.IsLockRequested)
                    {
                        lockRequest.IsLockRequested = DataTransfer.WriteLockMovementAndWaitForStandstill(Channel);
                    }
                }
                else if (DataTransfer.WriteUnlock(Channel))
                {
                    lockRequest.Resolve(true);
                    PendingLockRequests.Dequeue();
                }
                return false;
            }

            // 7. Flush requests
            if (BufferedCodes.Count == 0 && PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(true);
                return false;
            }

            return false;
        }

        private bool BufferCode(QueuedCode queuedCode)
        {
            if (queuedCode.Code.Type == CodeType.MCode && queuedCode.Code.MajorNumber == 291)
            {
                int sParam = queuedCode.Code.Parameter('S', 0);
                if (sParam == 2 || sParam == 3)
                {
                    // This M291 call interrupts the G-code flow, wait for M292 next
                    WaitingForMessageAcknowledgement = true;
                }
            }
            else if (queuedCode.Code.Type == CodeType.MCode && queuedCode.Code.MajorNumber == 292)
            {
                // The pending message box is about to be closed
                WaitingForMessageAcknowledgement = false;
            }
            else if (WaitingForMessageAcknowledgement)
            {
                // Still waiting for M292...
                return false;
            }

            // Try to send this code to the firmware
            try
            {
                if (Interface.BufferCode(queuedCode, out int codeLength))
                {
                    BytesBuffered += codeLength;
                    BufferedCodes.Add(queuedCode);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                queuedCode.SetException(e);
                return true;
            }
        }

        private string _partialLogMessage;

        /// <summary>
        /// Handle a G-code reply
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Code reply</param>
        /// <returns>Whether the reply could be processed</returns>
        public bool HandleReply(MessageTypeFlags flags, string reply)
        {
            if (flags.HasFlag(MessageTypeFlags.LogMessage))
            {
                _partialLogMessage += reply;
                if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                {
                    if (!string.IsNullOrWhiteSpace(_partialLogMessage))
                    {
                        MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                                : MessageType.Success;
                        Utility.Logger.Log(type, _partialLogMessage);
                    }
                    _partialLogMessage = null;
                }
            }

            if (NestedMacros.TryPeek(out MacroFile macroFile) &&
                ((macroFile.StartCode != null && !macroFile.StartCode.DoingNestedMacro) || (macroFile.StartCode == null && SystemMacroHasFinished)))
            {
                if (macroFile.StartCode != null)
                {
                    macroFile.StartCode.HandleReply(flags, reply);
                    if (macroFile.IsFinished)
                    {
                        NestedMacros.Pop().Dispose();
                        Console.WriteLine($"[info] Completed macro {macroFile.FileName} + start code {macroFile.StartCode}");
                    }
                }
                else if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                {
                    NestedMacros.Pop().Dispose();
                    SystemMacroHasFinished = false;
                    Console.WriteLine($"[info] Completed system macro {macroFile.FileName}");
                }
                return true;
            }

            if (BufferedCodes.Count > 0)
            {
                BufferedCodes[0].HandleReply(flags, reply);
                if (BufferedCodes[0].IsFinished)
                {
                    BytesBuffered -= BufferedCodes[0].BinarySize;
                    BufferedCodes.RemoveAt(0);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempt to start a file macro
        /// </summary>
        /// <param name="filename">Name of the macro file</param>
        /// <param name="reportMissing">Report an error if the file could not be found</param>
        /// <param name="fromCode">Request comes from a real G/M/T-code</param>
        /// <returns>Asynchronous task</returns>
        public async Task HandleMacroRequest(string filename, bool reportMissing, bool fromCode)
        {
            // Get the code starting the macro file
            QueuedCode startingCode = null;
            if (fromCode)
            {
                if (NestedMacros.TryPeek(out MacroFile macroFile) && macroFile.StartCode != null && !macroFile.StartCode.DoingNestedMacro)
                {
                    // In case a G/M/T-code invokes more than one macro file...
                    startingCode = macroFile.StartCode;

                    // Check if the other macro file has been finished
                    if (macroFile.IsFinished)
                    {
                        NestedMacros.Pop().Dispose();
                        Console.WriteLine($"[info] Completed intermediate macro '{macroFile.FileName}'");
                    }
                }
                else if (BufferedCodes.Count > 0)
                {
                    // The top buffered code is the one that requested the macro file
                    startingCode = BufferedCodes[0];
                }

                if (startingCode != null)
                {
                    startingCode.DoingNestedMacro = true;
                }
            }

            // Locate the macro file
            string path = await FilePath.ToPhysicalAsync(filename, "sys");
            if (!File.Exists(path))
            {
                if (filename == FilePath.ConfigFile)
                {
                    path = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, "sys");
                    if (File.Exists(path))
                    {
                        // Use config.b.bak if config.g cannot be found
                        Console.WriteLine($"[warn] Using fallback file {FilePath.ConfigFileFallback} because {FilePath.ConfigFile} could not be found");
                    }
                    else
                    {
                        await Utility.Logger.LogOutput(MessageType.Error, $"Macro files {FilePath.ConfigFile} and {FilePath.ConfigFileFallback} not found");
                    }
                }
                else if (reportMissing)
                {
                    if (!fromCode || BufferedCodes.Count == 0 || BufferedCodes[0].Code.Type != CodeType.MCode || BufferedCodes[0].Code.MajorNumber != 98)
                    {
                        // M98 outputs its own warning message via RRF
                        await Utility.Logger.LogOutput(MessageType.Error, $"Macro file {filename} not found");
                    }
                }
                else
                {
                    Console.WriteLine($"[info] Optional macro file '{filename}' not found");
                }

                SuspendBuffer(startingCode);
                MacroCompleted(startingCode, true);
                return;
            }

            // Open the file
            try
            {
                MacroFile macro = new MacroFile(path, Channel, startingCode);
                NestedMacros.Push(macro);
            }
            catch (Exception e)
            {
                await Utility.Logger.LogOutput(MessageType.Error, $"Failed to open macro file '{filename}': {e.Message}");

                SuspendBuffer(startingCode);
                MacroCompleted(startingCode, true);
                return;
            }

            // Macro file is now running. At this point, the buffered codes have been thrown away by RRF
            SuspendBuffer();
        }

        private bool MacroCompleted(QueuedCode startingCode, bool error)
        {
            if (DataTransfer.WriteMacroCompleted(Channel, error))
            {
                _resumingBuffer = true;
                if (startingCode != null)
                {
                    startingCode.DoingNestedMacro = false;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Indicates if the suspended codes are being resumed
        /// </summary>
        private bool _resumingBuffer;

        /// <summary>
        /// Invalidate all the buffered G/M/T-codes
        /// </summary>
        /// <param name="invalidateLastFileCodes)">Invalidate only codes of the last stack level</param>
        public void InvalidateBuffer(bool invalidateLastFileCodes)
        {
            if (invalidateLastFileCodes)
            {
                // Only a G/M-code can ask for last file aborts. It is still being executed, so take care of it 
                for (int i = BufferedCodes.Count - 1; i > 0; i--)
                {
                    BytesBuffered -= BufferedCodes[i].BinarySize;
                    BufferedCodes.RemoveAt(i);
                }

                if (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
                {
                    while (suspendedCodes.TryDequeue(out QueuedCode suspendedCode))
                    {
                        suspendedCode.SetCancelled();
                    }
                }
            }
            else
            {
                // Remove every buffered code of this channel if every code is being invalidated
                foreach (QueuedCode queuedCode in BufferedCodes)
                {
                    queuedCode.SetCancelled();
                }

                BytesBuffered = 0;
                BufferedCodes.Clear();

                while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
                {
                    while (suspendedCodes.TryDequeue(out QueuedCode suspendedCode))
                    {
                        suspendedCode.SetCancelled();
                    }
                }
            }

            _resumingBuffer = invalidateLastFileCodes;
            SystemMacroHasFinished = false;

            // Resolve pending flush requests. At this point, codes waiting for a flush must stop executing
            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
            }

            // Do not send codes to RRF until it has cleared its internal buffer
            IsBlocked = true;
        }

        /// <summary>
        /// Suspend all the buffered G/M/T-codes for future execution
        /// </summary>
        /// <param name="codeBeingExecuted">Current code being executed to leave in the buffer</param>
        public void SuspendBuffer(QueuedCode codeBeingExecuted = null)
        {
            Queue<QueuedCode> suspendedItems = new Queue<QueuedCode>();

            // Suspend the remaining buffered codes except for the one that is already being executed
            foreach (QueuedCode bufferedCode in BufferedCodes.ToList())
            {
                if (bufferedCode != codeBeingExecuted)
                {
                    if (!bufferedCode.DoingNestedMacro)
                    {
                        Console.WriteLine($"Suspending code {bufferedCode}");
                        bufferedCode.IsSuspended = true;
                        suspendedItems.Enqueue(bufferedCode);
                    }

                    BytesBuffered -= bufferedCode.BinarySize;
                    BufferedCodes.Remove(bufferedCode);
                }
            }

            // Deal with case of a nested macro being started while suspended codes are still being restored
            if (_resumingBuffer)
            {
                if (SuspendedCodes.TryPop(out Queue<QueuedCode> remainingSuspendedCodes))
                {
                    while (remainingSuspendedCodes.TryDequeue(out QueuedCode remainingCode))
                    {
                        suspendedItems.Enqueue(remainingCode);
                    }
                }
                _resumingBuffer = false;
            }

            // Enequeue the suspended codes so they can continue execution later on
            SuspendedCodes.Push(suspendedItems);

            // Do not send codes to RRF until it has cleared its internal buffer
            IsBlocked = true;
        }

        /// <summary>
        /// Resume suspended codes when a nested macro file has finished
        /// </summary>
        /// <returns>True when finished</returns>
        public void ResumeBuffer()
        {
            if (SuspendedCodes.TryPeek(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryPeek(out QueuedCode suspendedCode))
                {
                    if (BufferCode(suspendedCode))
                    {
                        Console.WriteLine($"-> Resumed suspended code");
                        suspendedCode.IsSuspended = false;
                        suspendedCodes.Dequeue();
                    }
                    else
                    {
                        return;
                    }
                }
                SuspendedCodes.Pop();
            }
            _resumingBuffer = false;
        }

        /// <summary>
        /// Invalidate every request and buffered code on this channel
        /// </summary>
        /// <returns>If any resource has been invalidated</returns>
        public bool Invalidate()
        {
            bool resourceInvalidated = false;

            Commands.Code.CancelPending(Channel);

            while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryDequeue(out QueuedCode queuedCode))
                {
                    queuedCode.SetCancelled();
                    resourceInvalidated = true;
                }
            }
            _resumingBuffer = false;
            WaitingForMessageAcknowledgement = SystemMacroHasFinished = false;

            while (NestedMacroCodes.TryDequeue(out QueuedCode item))
            {
                if (!item.IsFinished)
                {
                    item.SetCancelled();
                    resourceInvalidated = true;
                }
            }

            while (NestedMacros.TryPop(out MacroFile macroFile))
            {
                macroFile.StartCode?.SetCancelled();
                macroFile.Abort();
                macroFile.Dispose();
                resourceInvalidated = true;
            }

            while (PendingCodes.TryDequeue(out QueuedCode queuedCode))
            {
                queuedCode.SetCancelled();
                resourceInvalidated = true;
            }

            while (PendingLockRequests.TryDequeue(out QueuedLockRequest item))
            {
                item.Resolve(false);
                resourceInvalidated = true;
            }

            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
                resourceInvalidated = true;
            }

            foreach (QueuedCode bufferedCode in BufferedCodes)
            {
                bufferedCode.SetCancelled();
                resourceInvalidated = true;
            }
            BufferedCodes.Clear();

            IsBlocked = true;
            BytesBuffered = 0;

            return resourceInvalidated;
        }
    }

    /// <summary>
    /// Class used to hold internal information about all the code channels
    /// </summary>
    public class ChannelStore : IEnumerable<ChannelInformation>
    {
        private readonly ChannelInformation[] _channels;

        /// <summary>
        /// Constructor of the channel store
        /// </summary>
        public ChannelStore()
        {
            CodeChannel[] channels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

            _channels = new ChannelInformation[channels.Length];
            foreach (CodeChannel channel in channels)
            {
                this[channel] = new ChannelInformation(channel);
            }
        }

        /// <summary>
        /// Index operator for easy access via a CodeChannel value
        /// </summary>
        /// <param name="channel">Channel to retrieve information about</param>
        /// <returns>Information about the code channel</returns>
        public ChannelInformation this[CodeChannel channel]
        {
            get => _channels[(int)channel];
            set => _channels[(int)channel] = value;
        }

        /// <summary>
        /// Reset busy channels
        /// </summary>
        public void ResetBlockedChannels()
        {
            foreach (ChannelInformation channel in _channels)
            {
                channel.IsBlocked = false;
            }
        }

        /// <summary>
        /// Implementation of the GetEnumerator method
        /// </summary>
        /// <returns></returns>
        IEnumerator IEnumerable.GetEnumerator() => _channels.GetEnumerator();

        /// <summary>
        /// Implementation of the GetEnumerator method
        /// </summary>
        /// <returns></returns>
        IEnumerator<ChannelInformation> IEnumerable<ChannelInformation>.GetEnumerator() => ((IEnumerable<ChannelInformation>)_channels).GetEnumerator();
    }
}
