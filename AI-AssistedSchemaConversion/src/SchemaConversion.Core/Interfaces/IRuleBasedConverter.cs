using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Core.Interfaces;

public interface IRuleBasedConverter
{
    ConversionResult Convert(SchemaObject obj, ConversionContext context);
}
