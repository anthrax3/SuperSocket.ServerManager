﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SuperSocket.Management.AgentClient.Config;
using System.Dynamic;
using SuperSocket.Management.AgentClient.ViewModel;

namespace SuperSocket.Management.AgentClient
{
    /// <summary>
    /// Interaction logic for ConfigPanel.xaml
    /// </summary>
    public partial class ConfigPanel : UserControl
    {
        public ConfigPanel()
        {
            InitializeComponent();
        }

        private void Grid_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var viewModel = e.NewValue as NodeConfigViewModel;
            Password.Password = viewModel.Password ?? string.Empty;
       }
    }
}
