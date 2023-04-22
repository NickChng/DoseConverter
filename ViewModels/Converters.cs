using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ESAPIScript;

namespace EQD2Converter
{
   
     public class SortStructuresConverter : IMultiValueConverter
    {
        // Multivalue converter example sorting structures in a list according to how closesly they match a comparator, using Levenshtein Distance
        public object Convert(object[] value, Type targetType,
               object parameter, System.Globalization.CultureInfo culture)
        {
            string matchString = (value[0] as string);
            ObservableCollection<string> AvailableOptions = value[1] as ObservableCollection<string>;
            return Helpers.sortOptions(matchString, AvailableOptions);
        }

        

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class VisibilityConverter : IValueConverter
    {
        // Convert a boolean into a visibility setting
        public object Convert(object value, Type targetType,
               object parameter, System.Globalization.CultureInfo culture)
        {
            bool? V = value as bool?;
            if (V == null)
                return Visibility.Collapsed;
            if (V == true)
                return Visibility.Visible;
            else
                return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetTypes,
               object parameter, System.Globalization.CultureInfo culture)
        {
            Visibility? V = value as Visibility?;
            if (V == Visibility.Hidden)
                return false;
            else
                return true;
        }
    }
    public class VisibilityInverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
               object parameter, System.Globalization.CultureInfo culture)
        {
            bool? V = value as bool?;
            if (V == null)
                return Visibility.Collapsed;
            if (V == false)
                return Visibility.Visible;
            else
                return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetTypes,
               object parameter, System.Globalization.CultureInfo culture)
        {
            Visibility? V = value as Visibility?;
            if (V == Visibility.Hidden)
                return false;
            else
                return true;
        }
    }
    public class VisibilityMultiConverter : IMultiValueConverter
    {
        // Example of how to set visibility based on multiple inputs
        public object Convert(object[] value, Type targetType,
              object parameter, System.Globalization.CultureInfo culture)
        {
            foreach (bool v in value)
            {
                if (v)
                    return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EnumDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
              object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is Enum)
            {
                return (value as Enum).Display();
            }
            else
                return "";
            
            
        }
        public object ConvertBack(object value, Type targetTypes,
               object parameter, System.Globalization.CultureInfo culture)
        {
           throw new NotImplementedException();
        }
    }
        
}
