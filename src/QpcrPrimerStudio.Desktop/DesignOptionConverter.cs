using System.Globalization;
using System.Windows.Data;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public sealed class DesignOptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TargetRegionMode.EntireTranscript => "完整转录本",
        TargetRegionMode.CdsOnly => "仅 CDS 编码区",
        TargetRegionMode.ThreePrimeUtr => "3′ UTR",
        TargetRegionMode.FivePrimeUtr => "5′ UTR",
        TargetRegionMode.PreferCds => "优先 CDS 编码区",
        TargetRegionMode.AvoidUtr => "避开 UTR",
        ExpressionMode.TemplateOnly => "当前模板",
        ExpressionMode.TotalGene => "基因总表达",
        ExpressionMode.IsoformSpecific => "转录本特异检测",
        ExpressionMode.SelectedTranscripts => "指定转录本集合",
        _ => value.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
