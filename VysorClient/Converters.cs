using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VysorClient;

// Mostra o elemento (ex: texto de "carregando...") quando o valor vinculado é nulo,
// e esconde quando já existe uma imagem/frame. Usado nas miniaturas do modal de
// compartilhamento e nos tiles de transmissão.
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
