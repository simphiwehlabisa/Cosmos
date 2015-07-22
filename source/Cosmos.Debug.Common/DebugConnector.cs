﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cosmos.Debug.Common;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Cosmos.Debug.Common
{
    /// <summary>Handles the dialog between the Debug Stub embedded in a debugged Cosmos Kernel and
    /// our Debug Engine hosted in Visual Studio. This abstract class is communication protocol
    /// independent. Sub-classes exist that manage the wire level details of the communications.
    /// </summary>
    public abstract partial class DebugConnector : IDisposable
    {
        // MtW: This class used to use async (BeginRead/EndRead) reading, which gave some weird bug.
        // The issue was that randomly it wouldn't return any data anymore, resulting in freezes of the kernel.

        public Action<Exception> ConnectionLost;
        public Action<UInt32> CmdTrace;
        public Action<UInt32> CmdBreak;
        public Action<byte[]> CmdMethodContext;
        public Action<string> CmdText;
        public Action<string> CmdMessageBox;
        public Action CmdStarted;
        public Action<string> OnDebugMsg;
        public Action<byte[]> CmdRegisters;
        public Action<byte[]> CmdFrame;
        public Action<byte[]> CmdStack;
        public Action<byte[]> CmdPong;
        public Action<byte, byte, byte[]> CmdChannel;
        public Action<UInt32> CmdStackCorruptionOccurred;
        public Action<UInt32> CmdNullReferenceOccurred;
        public Action<Exception> Error;

        protected byte mCurrentMsgType;
        protected AutoResetEvent mCmdWait = new AutoResetEvent(false);

        private StreamWriter mDebugWriter = StreamWriter.Null; //new StreamWriter(@"e:\dcdebug.txt", false) { AutoFlush = true };

        // This member used to be public. The SetConnectionHandler has been added.
        private Action Connected;

        protected void HandleError(Exception E)
        {
            if (Error != null)
            {
                Error(E);
            }
            else
            {
                throw new Exception("Unhandled exception occurred!", E);
            }
        }

        /// <summary>Descendants must invoke this method whenever they detect an incoming connection.</summary>
        public void DoConnected()
        {
            if (Connected != null)
            {
                Connected();
            }
        }

        /// <summary>Defines the handler to be invoked when a connection occurs on this condector. This
        /// method is for use by the AD7Process instance.</summary>
        /// <param name="handler">The handler to be notified when a connection occur.</param>
        public void SetConnectionHandler(Action handler)
        {
            Connected = handler;
        }

        protected virtual void DoDebugMsg(string aMsg)
        {
            //Console.WriteLine(aMsg);
            //System.Diagnostics.Debug.WriteLine(aMsg);
            // MtW: Copying mDebugWriter and mOut to local variables may seem weird, but in some situations, this method can be called when they are null.
            var xStreamWriter = mDebugWriter;
            if (xStreamWriter != null)
            {
                xStreamWriter.WriteLine(aMsg);
                xStreamWriter.Flush();
            }
            var xWriter = mOut;
            if (xWriter != StreamWriter.Null)
            {
                xWriter.WriteLine(aMsg);
                xWriter.Flush();
            }
            //DoDebugMsg(aMsg, false);
        }

        //private static StreamWriter mOut = new StreamWriter(@"c:\data\sources\dcoutput.txt", false)
        //                            {
        //                                AutoFlush = true
        //                            };
        private static StreamWriter mOut = StreamWriter.Null;

        protected void DoDebugMsg(string aMsg, bool aOnlyIfConnected)
        {
            if (IsConnected || aOnlyIfConnected == false)
            {
                System.Diagnostics.Debug.WriteLine(aMsg);
                if (OnDebugMsg != null)
                {
                    OnDebugMsg(aMsg);
                }
            }
        }

        protected bool mSigReceived = false;
        public bool SigReceived
        {
            get { return mSigReceived; }
        }

        // Is stream alive? Other side may not be responsive yet, use SigReceived instead for this instead
        public abstract bool IsConnected
        {
            get;
        }

        private int mDataSize;
        private List<byte[]> MethodContextDatas = new List<byte[]>();
        private List<byte[]> MemoryDatas = new List<byte[]>();

        public void DeleteBreakpoint(int aID)
        {
            SetBreakpoint(aID, 0);
        }

        protected UInt32 GetUInt32(byte[] aBytes, int aOffset)
        {
            return (UInt32)((aBytes[aOffset + 3] << 24) | (aBytes[aOffset + 2] << 16)
               | (aBytes[aOffset + 1] << 8) | aBytes[aOffset + 0]);
        }

        protected UInt16 GetUInt16(byte[] aBytes, int aOffset)
        {
            return (UInt16)((aBytes[aOffset + 1] << 8) | aBytes[aOffset + 0]);
        }

        protected void PacketMsg(byte[] aPacket)
        {
            mCurrentMsgType = aPacket[0];

            //DoDebugMsg(String.Format("DC - PacketMsg: {0}", DebugConnectorStreamWithTimeouts.BytesToString(aPacket, 0, aPacket.Length)));
            //DoDebugMsg("DC - " + mCurrentMsgType);
            // Could change to an array, but really not much benefit
            switch (mCurrentMsgType)
            {
                case Ds2Vs.TracePoint:
                    DoDebugMsg("DC Recv: TracePoint");
                    Next(4, PacketTracePoint);
                    break;

                case Ds2Vs.BreakPoint:
                    DoDebugMsg("DC Recv: BreakPoint");
                    Next(4, PacketBreakPoint);
                    break;

                case Ds2Vs.Message:
                    DoDebugMsg("DC Recv: Message");
                    Next(2, PacketTextSize);
                    break;

                case Ds2Vs.MessageBox:
                    DoDebugMsg("DC Recv: MessageBox");
                    Next(2, PacketMessageBoxTextSize);
                    break;

                case Ds2Vs.Started:
                    DoDebugMsg("DC Recv: Started");
                    // Call WaitForMessage first, else it blocks because Ds2Vs.Started triggers
                    // other commands which need responses.
                    WaitForMessage();

                    // Guests never get the first byte sent. So we send a noop.
                    // This dummy byte seems to clear out the serial channel.
                    // Its never received, but if it ever is, its a noop anyways.
                    SendCmd(Vs2Ds.Noop);

                    // Send signature
                    var xData = new byte[4];
                    Array.Copy(BitConverter.GetBytes(Cosmos.Debug.Common.Consts.SerialSignature), 0, xData, 0, 4);
                    SendRawData(xData);

                    CmdStarted();
                    break;

                case Ds2Vs.Noop:
                    DoDebugMsg("DC Recv: Noop");
                    // MtW: When implementing Serial support for debugging on real hardware, it appears
                    //      that when booting a machine, in the bios it emits zero's to the serial port.
                    // Kudzu: Made a Noop command to handle this
                    WaitForMessage();
                    break;

                case Ds2Vs.CmdCompleted:
                    DoDebugMsg("DC Recv: CmdCompleted");
                    Next(1, PacketCmdCompleted);
                    break;

                case Ds2Vs.MethodContext:
                    DoDebugMsg("DC Recv: MethodContext");
                    Next(mDataSize, PacketMethodContext);
                    break;

                case Ds2Vs.MemoryData:
                    DoDebugMsg("DC Recv: MemoryData");
                    Next(mDataSize, PacketMemoryData);
                    break;

                case Ds2Vs.Registers:
                    DoDebugMsg("DC Recv: Registers");
                    Next(40, PacketRegisters);
                    break;

                case Ds2Vs.Frame:
                    DoDebugMsg("DC Recv: Frame");
                    Next(-1, PacketFrame);
                    break;

                case Ds2Vs.Stack:
                    DoDebugMsg("DC Recv: Stack");
                    Next(-1, PacketStack);
                    break;

                case Ds2Vs.Pong:
                    DoDebugMsg("DC Recv: Pong");
                    Next(0, PacketPong);
                    break;

                case Ds2Vs.StackCorruptionOccurred:
                    DoDebugMsg("DC Recv: StackCorruptionOccurred");
                    Next(4, PacketStackCorruptionOccurred);
                    break;

                case Ds2Vs.NullReferenceOccurred:
                    DoDebugMsg("DC Recv: NullReferenceOccurred");
                    Next(4, PacketNullReferenceOccurred);
                    break;
                default:
                    if (mCurrentMsgType > 128)
                    {
                        // other channels than debugstub
                        DoDebugMsg("DC Recv: Console");
                        // copy to local variable, so the anonymous method will get the correct value!
                        var xChannel = mCurrentMsgType;
                        Next(1, data => PacketOtherChannelCommand(xChannel, data));
                        break;
                    }
                    // Exceptions crash VS so use MsgBox instead
                    DoDebugMsg("Unknown debug command: " + mCurrentMsgType);
                    // Despite it being unkonwn, we try again. Normally this will
                    // just cause more unknowns, but can be useful for debugging.
                    WaitForMessage();
                    break;
            }
        }

        public virtual void Dispose()
        {
            if (mDebugWriter != null)
            {
                mDebugWriter.Dispose();
                mDebugWriter = null;
            }
            if (mBackgroundThread != null)
            {
                mBackgroundThread.Abort();
                mBackgroundThread.Join();
                mBackgroundThread = null;
            }
            GC.SuppressFinalize(this);
        }

        // Signature is sent after garbage emitted during init of serial port.
        // For more info see note in DebugStub where signature is transmitted.
        protected byte[] mSigCheck = new byte[4] { 0, 0, 0, 0 };
        protected virtual void WaitForSignature(byte[] aPacket)
        {
            mSigCheck[0] = mSigCheck[1];
            mSigCheck[1] = mSigCheck[2];
            mSigCheck[2] = mSigCheck[3];
            mSigCheck[3] = aPacket[0];
            var xSig = GetUInt32(mSigCheck, 0);
            DoDebugMsg("DC: Sig Byte " + aPacket[0].ToString("X2").ToUpper() + " : " + xSig.ToString("X8").ToUpper());
            if (xSig == Consts.SerialSignature)
            {
                // Sig found, wait for messages
                mSigReceived = true;
                SendTextToConsole("SigReceived!");
                DoDebugMsg("SigReceived");
                WaitForMessage();
            }
            else
            {
                SendPacketToConsole(aPacket);
                // Sig not found, keep looking
                Next(1, WaitForSignature);
            }
        }

        protected void SendPacketToConsole(byte[] aPacket)
        {
            CmdChannel(129, 0, aPacket);
        }

        protected void SendTextToConsole(string aText)
        {
            SendPacketToConsole(Encoding.UTF8.GetBytes(aText));
        }


    }
}
