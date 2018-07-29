﻿using BSPEngine;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace ESP8266DebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for ESPxxGDBStubSettingsControl.xaml
    /// </summary>
    public partial class ESPxxGDBStubSettingsControl : UserControl, ICustomDebugMethodConfigurator
    {
        ESPxxGDBStubSettingsEditor _Editor;
        private IBSPConfiguratorHost _Host;

        public ESPxxGDBStubSettingsControl(IBSPConfiguratorHost host, ICustomSettingsTypeProvider provider)
        {
            host.InstallStyles(this);
            InitializeComponent();
            TypeProvider = provider;
            _Host = host;
        }

        public object Control => this;

        public object Configuration => _Editor?.Settings;

        public ICustomSettingsTypeProvider TypeProvider {get; private set; }

        public bool SupportsLiveVariables => false;

        public event EventHandler SettingsChanged;

        public void SetConfiguration(object configuration, KnownInterfaceInstance context)
        {
            DataContext = _Editor = new ESPxxGDBStubSettingsEditor(configuration as ESPxxGDBStubSettingsBase, context, _Host, false);
            _Editor.SettingsChanged += (s, e) => SettingsChanged?.Invoke(s, e);
        }

        public bool TryFixSettingsFromStubError(IGDBStubInstance stub) => false;

        public string ValidateSettings() => _Editor?.ValidateSettings();


        private void RaiseSettingsChangedEvent(object sender, TextChangedEventArgs e)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseSettingsChangedEvent(object sender, SelectionChangedEventArgs e)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void AddNewResource(object sender, RoutedEventArgs e)
        {
            _Editor.FLASHResources.Add(new FLASHResource());
        }

        private void BrowseResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
            {
                var dlg = new OpenFileDialog { Filter = "All files|*.*" };
                if (dlg.ShowDialog() == true)
                    rsrc.UpdatePath(dlg.FileName);
            }
        }

        private void RemoveResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
                _Editor.FLASHResources.Remove(rsrc);
        }

        private void RaiseSettingsChangedEvent(object sender, RoutedEventArgs e)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class ShowOnlyInListViewConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isInsidePopup = false;
            for (DependencyObject obj = value as DependencyObject; obj != null; obj = VisualTreeHelper.GetParent(obj))
            {
                if (obj is ComboBoxItem)
                {
                    isInsidePopup = true;
                    break;
                }
            }

            return isInsidePopup ? Visibility.Visible : Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
