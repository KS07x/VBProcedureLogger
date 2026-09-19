Imports System

''' <summary>
''' Marks a Visual Basic procedure for procedure logging.
''' </summary>
<AttributeUsage(AttributeTargets.Method, AllowMultiple:=False, Inherited:=False)>
Public NotInheritable Class LogAttribute
    Inherits Attribute

    Public Sub New()
        LogArguments = True
        LogReturnValue = True
        LogTimeStamp = True
        LogExecutionTime = False
    End Sub

    ''' <summary>Gets or sets whether input arguments are included in the log.</summary>
    Public Property LogArguments As Boolean

    ''' <summary>Gets or sets whether the return value is included in the log.</summary>
    Public Property LogReturnValue As Boolean

    ''' <summary>Gets or sets whether each entry is prefixed with a timestamp.</summary>
    Public Property LogTimeStamp As Boolean

    ''' <summary>Gets or sets whether execution duration is included in the completion entry.</summary>
    Public Property LogExecutionTime As Boolean

    ''' <summary>
    ''' Gets or sets an optional path for this procedure's log file. Relative paths are
    ''' resolved from the application's execution directory.
    ''' </summary>
    Public Property LogFilePath As String

    Friend Function ToOptions() As LogOptions
        Return New LogOptions With {
            .LogArguments = LogArguments,
            .LogReturnValue = LogReturnValue,
            .LogTimeStamp = LogTimeStamp,
            .LogExecutionTime = LogExecutionTime,
            .LogFilePath = LogFilePath
        }
    End Function
End Class
