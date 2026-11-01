using ClassMonitor.Shared.Management;

namespace ClassMonitor.Shared.Sessions;

public sealed record PairedDevice(string Host, int Port, DeviceEnrollment Enrollment, string PublicCertificateBase64);
public sealed record ManagedEnrollment(EnrollmentPackage Package, string DeviceFingerprint, string TeacherFingerprint);
public sealed record StateRequest(string RequestId, bool Active);
public sealed record StateResult(string RequestId, bool Ok, string Message);
public sealed record SessionHealth(string SessionId, bool Active, bool Assisting, bool Recording);

public static class CommandPolicy
{
    public static ClassroomPermission Required(MsgType type, byte[] payload) => type switch
    {
        MsgType.ScreenRequest => ClassroomPermission.View,
        MsgType.RemoteInput or MsgType.AssistanceState => ClassroomPermission.Assist,
        MsgType.TeacherFrame or MsgType.TeacherBroadcast => ClassroomPermission.Present,
        MsgType.ShowMessage or MsgType.Chat or MsgType.Question or MsgType.Answer => ClassroomPermission.Message,
        MsgType.FileSend or MsgType.FileRequest => ClassroomPermission.Files,
        MsgType.Lock or MsgType.Restrict => ClassroomPermission.Focus,
        MsgType.RecordingState => ClassroomPermission.Record,
        MsgType.Power => ClassroomPermission.Admin,
        MsgType.Run => Frame.ReadJson<RunCmd>(payload).Kind == "website" ? ClassroomPermission.Message : ClassroomPermission.Admin,
        _ => throw new UnauthorizedAccessException("Unsupported operation")
    };
}
