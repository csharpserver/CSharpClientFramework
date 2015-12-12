﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;
using CSharpClientFramework.Client;
using CSharpClientFramework.Util;

namespace CSharpClientFramework
{
    public class CSharpServerClientBase : IDisposable
    {
        protected TcpClient Client { get; set; }
        private byte[] receiveBuffer = new byte[16 * 1024];
        private volatile bool _isRunning = false;
        protected EventHandlerList Events;
        private readonly int TCP_PACKAGE_HEAD_SIZE = 4;

        protected IDictionary<string, object> HandlerKeyMap { get; private set; }

        public event EventHandler<CSharpServerClientEventArgs> OnConnected;
        public event EventHandler<CSharpServerClientEventArgs> OnDisconnected;
        public event EventHandler<CSharpServerClientEventArgs> OnSendFailed;
        public event EventHandler<CSharpServerClientReceiveMessageEventArgs> OnMessageReceived;
        public IDeserializeMessage MessageDepressor { get; set; }

        public CSharpServerClientBase(IDeserializeMessage MessageDepressor)
        {
            this.MessageDepressor = MessageDepressor;
            Init();
        }

        private void Init()
        {
            Events = new EventHandlerList();
            HandlerKeyMap = new Dictionary<string, object>();
        }

        public void SetBufferSize(int NewLength)
        {
            receiveBuffer = new byte[NewLength];
        }

        public void AddHandlerCallback(string ExtensionName, object Command, EventHandler<CSharpServerClientEventArgs> Callback)
        {
            object key = GenerateKey(ExtensionName, Command);
            Events.AddHandler(key, Callback);
        }

        private object GenerateKey(string ExtensionName, object Command)
        {
            var cmd = GenerateCmdValue(Command);
            string key = GenerateCmdKey(ExtensionName, cmd);
            if (HandlerKeyMap.ContainsKey(key))
            {
                return HandlerKeyMap[key];
            }
            else
            {
                HandlerKeyMap[key] = key;
            }
            return key;
        }

        private string GenerateCmdKey(string ExtensionName, string cmdValue)
        {
            string key = string.Format("On{0}_{1}", ExtensionName, cmdValue);
            return key;
        }

        private string GenerateCmdValue(object Command)
        {
            string cmd = null;
            if (Command is int)
            {
                cmd = string.Format("CmdId({0})", Command);
            }
            else
            {
                cmd = Command.ToString();
            }
            return cmd;
        }

        protected bool IsRunning
        {
            get { return _isRunning; }
            private set { _isRunning = value; }
        }


        public void Start(IPAddress Ip, int Port)
        {
            Client = new TcpClient();
            _isRunning = false;
            Client.BeginConnect(Ip, Port, ConnectCallback, Client);
        }


        protected void DispatcherEvent(EventHandler<CSharpServerClientEventArgs> handler, CSharpServerClientEventArgs args)
        {
            if (handler == null) return;
            object[] param = new object[] { this, args };
            EventDispatcherUtil.AsyncDispatcherEvent(handler, this, args);
        }

        protected void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                IsRunning = true;
                ReceiveHead();
                DispatchClientConnected();
            }
            catch (Exception ex)
            {
                DispatchSendFailed(ex, "Remote Server Not Response");
                DispatchClientDisconnected();
                return;
            }
        }


        public void SendMessageAsync(byte[] Data, int Length)
        {
            byte[] sendData = new byte[Data.Length + 4];
            int len = BitUtil.CreateDataPackageWithHead(sendData, Data, Length);
            try
            {
                Client.Client.BeginSend(sendData, 0, len, SocketFlags.None, OnSendCallback, Client);
            }
            catch (Exception ex)
            {
                DispatchSendFailed(ex, "Send Message Failed");
            }

        }


        public void SendMessage(byte[] Data, int Length)
        {
            byte[] sendData = new byte[Length + 4];
            int len = BitUtil.CreateDataPackageWithHead(sendData, Data, Length);
            try
            {
                Client.Client.Send(sendData, 0, len, SocketFlags.None);
            }
            catch (Exception ex)
            {
                DispatchSendFailed(ex, "Send Message Failed");
            }

        }

        protected void OnSendCallback(IAsyncResult ar)
        {
            int len = Client.Client.EndSend(ar);
        }

        private void ReceiveHead()
        {
            try
            {
                Client.Client.BeginReceive(receiveBuffer, 0, 4, SocketFlags.None, DoReveivePackageHead, null);
            }
            catch (Exception)
            {
                

            }
        }

        private void DoReveivePackageHead(IAsyncResult ar)
        {
            SocketError se = SocketError.TimedOut;
            int len;
            try
            {
                len = Client.Client.EndReceive(ar, out se);
                if (TCP_PACKAGE_HEAD_SIZE == len)
                {
                    int packLen = BitConverter.ToInt32(receiveBuffer, 0);
                    ///开始接收实际数据包
                    ReceiveData(packLen);
                }
                else
                {
                    
                }
            }
            catch (Exception)
            {
                
            }
        }

        private void ReceiveData(int PackageLength)
        {
            Client.Client.BeginReceive(receiveBuffer, 0, PackageLength, SocketFlags.None, DoClientLoopReceiveCallback, PackageLength);
        }

        private void DoClientLoopReceiveCallback(IAsyncResult ar)
        {
            SocketError se = SocketError.TimedOut;
            int packLen = (int)ar.AsyncState;
            int len;
            try
            {
                len = Client.Client.EndReceive(ar, out se);
                if (len == packLen)
                {
                    try
                    {
                        CSharpServerClientBaseMessage msg = MessageDepressor.GetMessageFromBuffer(receiveBuffer, len);
                        CSharpServerClientEventArgs args = new CSharpServerClientEventArgs();
                        args.State = msg;
                        object handlerKey = null;
                        if (string.IsNullOrWhiteSpace(msg.CommandName))
                        {
                            handlerKey = GenerateKey(msg.Extension, msg.CommandId);
                        }
                        else
                        {
                            handlerKey = GenerateKey(msg.Extension, msg.CommandName);
                        }
                        object eventHandler = Events[handlerKey];
                        EventHandler<CSharpServerClientEventArgs> handler = eventHandler as EventHandler<CSharpServerClientEventArgs>;
                        if (handler != null)
                        {
                            DispatcherEvent(handler, args);
                        }
                    }
                    catch (Exception)
                    {

                    }
                    finally
                    {
                        var receiveBufferCopy = new byte[len];
                        Array.Copy(receiveBuffer, receiveBufferCopy, len);
                        EventDispatcherUtil.AsyncDispatcherEvent(OnMessageReceived, this, new CSharpServerClientReceiveMessageEventArgs() { ReceiveMessage = receiveBufferCopy });
                        ReceiveHead();
                    }
                }
            }
            catch (Exception)
            {
                
            }
        }

        private void DispatchClientConnected()
        {
            if (OnConnected != null)
            {
                EventDispatcherUtil.DispatcherEvent(this.OnConnected, this, new CSharpServerClientEventArgs());
            }
        }

        private void DispatchClientDisconnected()
        {
            if(OnDisconnected != null)
            {
                EventDispatcherUtil.DispatcherEvent(this.OnDisconnected, this, new CSharpServerClientEventArgs());
            }
        }

        private void DispatchSendFailed(Exception ex, string Message = null)
        {
            object[] param = new object[]
            {
                this,
                new CSharpServerClientEventArgs()
                {
                    State = ex
                }
            };
            var handler = Events["OnSendFailed"];
            if (this.OnSendFailed != null)
            {
                EventDispatcherUtil.DispatcherEvent(this.OnSendFailed, this, new CSharpServerClientEventArgs());
            }
        }


        public void Close()
        {
            if (IsRunning && Client != null && Client.Connected)
            {
                IsRunning = false;
                DispatchClientDisconnected();
                Client.Close();
            }
        }


        #region IDisposable 成员


        public void Dispose()
        {
            Close();
            Events.Dispose();
        }



        #endregion
    }

    public interface IDeserializeMessage
    {
        CSharpServerClientBaseMessage GetMessageFromBuffer(byte[] receiveBuffer, int len);
    }

    public class CSharpServerClientBaseMessage
    {
        public string Extension { get; set; }
        public int CommandId { get; set; }
        public string CommandName { get; set; }
    }

}

