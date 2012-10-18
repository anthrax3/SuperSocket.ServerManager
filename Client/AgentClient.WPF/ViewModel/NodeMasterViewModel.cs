﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicViewModel;
using Newtonsoft.Json.Linq;
using SuperSocket.ClientEngine;
using SuperSocket.Management.AgentClient.Command;
using SuperSocket.Management.AgentClient.Config;
using SuperSocket.Management.AgentClient.Metadata;
using SuperSocket.Management.Server.Model;
using WebSocket4Net;

namespace SuperSocket.Management.AgentClient.ViewModel
{
    public partial class NodeMasterViewModel : ViewModelBase
    {
        private AgentWebSocket m_WebSocket;
        private NodeConfig m_Config;
        private StateFieldMetadata[] m_FieldMetadatas;

        private bool m_LoginFailed = false;

        private ClientFieldAttribute[] m_ColumnAttributes;

        private ClientFieldAttribute[] m_NodeDetailAttributes;

        private Timer m_ReconnectTimer;

        public NodeConfig Config
        {
            get { return m_Config; }
        }

        public NodeMasterViewModel(NodeConfig config)
        {
            m_Config = config;
            Name = m_Config.Name;
            ConnectCommand = new DelegateCommand(ExecuteConnectCommand);

            try
            {
                m_WebSocket = new AgentWebSocket(config.Uri);
            }
            catch (Exception e)
            {
                ErrorMessage = e.Message;
                return;
            }

#if !SILVERLIGHT
            m_WebSocket.AllowUnstrustedCertificate = true;
            m_WebSocket.Closed += new EventHandler(WebSocket_Closed);
            m_WebSocket.Error += new EventHandler<ClientEngine.ErrorEventArgs>(WebSocket_Error);
            m_WebSocket.Opened += new EventHandler(WebSocket_Opened);
            m_WebSocket.On<string>(CommandName.UPDATE, OnServerUpdated);
#else
            m_WebSocket.ClientAccessPolicyProtocol = System.Net.Sockets.SocketClientAccessPolicyProtocol.Tcp;
            m_WebSocket.Closed += new EventHandler(CreateAsyncOperation<object, EventArgs>(WebSocket_Closed));
            m_WebSocket.Error += new EventHandler<ClientEngine.ErrorEventArgs>(CreateAsyncOperation<object, ClientEngine.ErrorEventArgs>(WebSocket_Error));
            m_WebSocket.Opened += new EventHandler(CreateAsyncOperation<object, EventArgs>(WebSocket_Opened));
            m_WebSocket.On<string>(CommandName.UPDATE, OnServerUpdatedAsync);
#endif
            StartConnect();
        }

        void StartConnect()
        {
            m_LoginFailed = false;

#if !SILVERLIGHT
            State = NodeState.Connecting;
#else
            if (Dispatcher.CheckAccess())
                State = NodeState.Connecting;
            else
                Dispatcher.BeginInvoke(() => State = NodeState.Connecting);
#endif
            m_WebSocket.Open();
        }

        void WebSocket_Opened(object sender, EventArgs e)
        {
            var websocket = sender as AgentWebSocket;
            State = NodeState.Logging;

            dynamic loginInfo = new ExpandoObject();
            loginInfo.UserName = m_Config.UserName;
            loginInfo.Password = m_Config.Password;

#if !SILVERLIGHT
            websocket.Query<dynamic>(CommandName.LOGIN, (object)loginInfo, OnLoggedIn);
#else
            websocket.Query<dynamic>(CommandName.LOGIN, (object)loginInfo, OnLoggedInAsync);
#endif
        }

        void OnLoggedIn(dynamic result)
        {
            if (result["Result"].ToObject<bool>())
            {                
                m_FieldMetadatas = result["FieldMetadatas"].ToObject<StateFieldMetadata[]>();
                var nodeInfo = DynamicViewModelFactory.Create(result["NodeInfo"].ToString());
                BuildGridColumns(m_FieldMetadatas);
                GlobalInfo = nodeInfo.GlobalInfo;
                var instances = nodeInfo.Instances as IEnumerable<DynamicViewModel.DynamicViewModel>;
                Instances = new ObservableCollection<DynamicViewModel.DynamicViewModel>(instances.Select(i =>
                    {
                        var startCommand = new DelegateCommand<DynamicViewModel.DynamicViewModel>(ExecuteStartCommand, CanExecuteStartCommand);
                        var stopCommand = new DelegateCommand<DynamicViewModel.DynamicViewModel>(ExecuteStopCommand, CanExecuteStopCommand);

                        i.PropertyChanged += (s, e) =>
                            {
                                if (string.IsNullOrEmpty(e.PropertyName)
                                    || e.PropertyName.Equals("IsRunning", StringComparison.OrdinalIgnoreCase))
                                {
                                    startCommand.RaiseCanExecuteChanged();
                                    stopCommand.RaiseCanExecuteChanged();
                                }
                            };

                        i.Set("StartCommand", startCommand);
                        i.Set("StopCommand", stopCommand);

                        return i;
                    }));
                State = NodeState.Connected;
                LastUpdatedTime = DateTime.Now;
            }
            else
            {
                m_LoginFailed = true;
                //login failed
                m_WebSocket.Close();
                ErrorMessage = "Logged in failed!";
            }
        }

        private bool CanExecuteStartCommand(DynamicViewModel.DynamicViewModel target)
        {
            return "False".Equals(((JValue)target["IsRunning"]).Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private void ExecuteStartCommand(DynamicViewModel.DynamicViewModel target)
        {
#if SILVERLIGHT
            m_WebSocket.Query<dynamic>(CommandName.START, ((JValue)target["Name"]).Value, OnActionCallbackAsync);
#else
            m_WebSocket.Query<dynamic>(CommandName.START, ((JValue)target["Name"]).Value, OnActionCallback);
#endif
        }

        private bool CanExecuteStopCommand(DynamicViewModel.DynamicViewModel target)
        {
            return "True".Equals(((JValue)target["IsRunning"]).Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private void ExecuteStopCommand(DynamicViewModel.DynamicViewModel target)
        {
#if SILVERLIGHT
            m_WebSocket.Query<dynamic>(CommandName.STOP, ((JValue)target["Name"]).Value, OnActionCallbackAsync);
#else
            m_WebSocket.Query<dynamic>(CommandName.STOP, ((JValue)target["Name"]).Value, OnActionCallback);
#endif
        }

        void OnServerUpdated(string result)
        {
            dynamic nodeInfo = DynamicViewModelFactory.Create(result);
            Dispatcher.BeginInvoke((Action<dynamic>)OnServerUpdated, nodeInfo);
        }

        void OnServerUpdated(dynamic nodeInfo)
        {
            this.GlobalInfo.UpdateProperties(nodeInfo.GlobalInfo);

            var instances = nodeInfo.Instances as IEnumerable<DynamicViewModel.DynamicViewModel>;

            foreach (var i in instances)
            {
                var targetInstance = m_Instances.FirstOrDefault(x =>
                    ((JValue)x["Name"]).Value.ToString().Equals(((JValue)i["Name"]).Value.ToString(), StringComparison.OrdinalIgnoreCase));

                if (targetInstance != null)
                {
                    targetInstance.UpdateProperties(i);
                    ((DelegateCommand<DynamicViewModel.DynamicViewModel>)targetInstance["StartCommand"]).RaiseCanExecuteChanged();
                    ((DelegateCommand<DynamicViewModel.DynamicViewModel>)targetInstance["StopCommand"]).RaiseCanExecuteChanged();
                }
            }

            LastUpdatedTime = DateTime.Now;
        }

        void OnActionCallback(string token, dynamic result)
        {
            if (result["Result"].ToObject<bool>())
            {
                var nodeInfo = ((JObject)result["NodeInfo"]).ToDynamic(new DynamicViewModel.DynamicViewModel());
                Dispatcher.BeginInvoke((Action<dynamic>)OnServerUpdated, nodeInfo);
            }
            else
            {
                ErrorMessage = result["Message"].ToString();
            }
        }

        void BuildGridColumns(StateFieldMetadata[] fieldMetadatas)
        {
            var dict = new Dictionary<string, ClientFieldAttribute>(StringComparer.OrdinalIgnoreCase);

            foreach (var metadata in fieldMetadatas)
            {
                if(metadata.InstanceNames == null || metadata.InstanceNames.Count == 0)
                {
                    m_NodeDetailAttributes = metadata.Fields;
                    continue;
                }

                foreach(var f in metadata.Fields)
                {
                    if (dict.ContainsKey(f.Name))
                        continue;

                    dict.Add(f.Name, f);
                }
            }

            m_ColumnAttributes = dict.Values.OrderBy(a => a.Order).ToArray();
        }

        void WebSocket_Error(object sender, ClientEngine.ErrorEventArgs e)
        {
            if (e.Exception != null)
            {
                if (e.Exception is SocketException && ((SocketException)e.Exception).ErrorCode == (int)SocketError.AccessDenied)
                    ErrorMessage = (new SocketException((int)SocketError.ConnectionRefused)).Message;
                else
                    ErrorMessage = e.Exception.Message;

                if (m_WebSocket.State == WebSocketState.None && State == NodeState.Connecting)
                {
                    State = NodeState.Offline;
                    OnDisconnected();
                }
            }
        }

        void WebSocket_Closed(object sender, EventArgs e)
        {
            State = NodeState.Offline;

            if (string.IsNullOrEmpty(ErrorMessage))
                ErrorMessage = "Offline";

            OnDisconnected();
        }

        void OnDisconnected()
        {
            //Don't reconnect if the client failed by login
            if (m_LoginFailed)
                return;

            if (m_ReconnectTimer == null)
            {
                m_ReconnectTimer = new Timer(ReconnectTimerCallback);
            }

            m_ReconnectTimer.Change(1000 * 60 * 5, Timeout.Infinite);//5 minutes
        }

        void ReconnectTimerCallback(object state)
        {
            //already open or openning
            if (m_WebSocket.State == WebSocketState.Connecting || m_WebSocket.State == WebSocketState.Open)
                return;

            StartConnect();
        }

        public string Name { get; private set; }

        private DateTime m_LastUpdatedTime;

        public DateTime LastUpdatedTime
        {
            get { return m_LastUpdatedTime; }
            set
            {
                m_LastUpdatedTime = value;
                RaisePropertyChanged("LastUpdatedTime");
            }
        }

        private string m_ErrorMessage;

        public string ErrorMessage
        {
            get { return m_ErrorMessage; }
            set
            {
                m_ErrorMessage = value;
                RaisePropertyChanged("ErrorMessage");
            }
        }

        private NodeState m_State = NodeState.Offline;

        public NodeState State
        {
            get { return m_State; }
            set
            {
                m_State = value;
                RaisePropertyChanged("State");
            }
        }

        public DelegateCommand ConnectCommand { get; private set; }

        private void ExecuteConnectCommand()
        {
            StartConnect();
        }

        private ObservableCollection<DynamicViewModel.DynamicViewModel> m_Instances;

        public ObservableCollection<DynamicViewModel.DynamicViewModel> Instances
        {
            get { return m_Instances; }
            set
            {
                m_Instances = value;
                RaisePropertyChanged("Instances");
            }
        }

        private DynamicViewModel.DynamicViewModel m_GlobalInfo;

        public DynamicViewModel.DynamicViewModel GlobalInfo
        {
            get { return m_GlobalInfo; }
            set
            {
                m_GlobalInfo = value;
                RaisePropertyChanged("GlobalInfo");
            }
        }

        public void DataGridLoaded(object sender, RoutedEventArgs e)
        {
            var grid = sender as DataGrid;

            var existingColumns = grid.Columns.Select(c => c.Header.ToString()).ToArray();

            foreach (var a in m_ColumnAttributes)
            {
                if (existingColumns.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                grid.Columns.Add(new DataGridTextColumn()
                    {
                        Header = a.Name,
                        Binding = new Binding(GetColumnValueBindingName(a.PropertyName))
                            {
                                StringFormat = string.IsNullOrEmpty(a.Format) ? "{0}" : a.Format
                            },
                        SortMemberPath = a.Name
                    });
            }
        }

        public void NodeDetailDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var grid = sender as Grid;

            var columns = 4;
            var rows = (int)Math.Ceiling((double)m_NodeDetailAttributes.Length / (double)columns);

            for (var i = 0; i < columns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            }

            for (var i = 0; i < rows; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition());
            }

            var k = 0;

            for (var i = 0; i < rows; i++)
            {
                for (var j = 0; j < columns; j++)
                {
                    var att = m_NodeDetailAttributes[k++];

                    var nameValuePanel = new StackPanel();
                    nameValuePanel.Orientation = Orientation.Horizontal;

                    var label = new TextBlock();
                    label.Style = App.Current.Resources["GlobalInfoLabel"] as Style;
                    label.Text = att.Name + ":";
                    nameValuePanel.Children.Add(label);

                    var value = new TextBlock();
                    value.Style = App.Current.Resources["GlobalInfoValue"] as Style;
                    value.SetBinding(TextBlock.TextProperty, new Binding(GetColumnValueBindingName(att.PropertyName))
                    {
                        StringFormat = string.IsNullOrEmpty(att.Format) ? "{0}" : att.Format
                    });
                    nameValuePanel.Children.Add(value);

                    nameValuePanel.SetValue(Grid.ColumnProperty, j);
                    nameValuePanel.SetValue(Grid.RowProperty, i);
                    grid.Children.Add(nameValuePanel);

                    if (k >= m_NodeDetailAttributes.Length)
                        break;
                }
            }
        }

#if !SILVERLIGHT
        private string GetColumnValueBindingName(string name)
        {
            return name;
        }

        private string GetColumnValueBindingName(string parent, string name)
        {
            return parent + "." + name;
        }
#else
        private string GetColumnValueBindingName(string name)
        {
            return string.Format("[{0}]", name);
        }

        private string GetColumnValueBindingName(string parent, string name)
        {
            return string.Format("[{0}][{1}]", parent, name);
        }
#endif
    }
}
