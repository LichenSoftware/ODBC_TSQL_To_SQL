using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Interfaces;

public interface IObjectClassifier
{
    ClassificationResult Classify(SchemaObject obj);
}
