using Avalonia.Data.Converters;
using GDMENUCardManager.Core;
using System;
using System.Globalization;

namespace GDMENUCardManager.Converter
{
    public class LocationEnumConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return ((LocationEnum)value).GetEnumName();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
