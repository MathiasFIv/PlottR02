using System.Text.Json.Serialization;

namespace PlottR0._2.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    SystemAdmin = 0,

    Researcher = 1,

    TrialManager = 2,

    FieldWorker = 3,
}
