Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Threading

''' <summary>
''' The options used for one logged procedure invocation.
''' </summary>
Public Class LogOptions
    Public Sub New()
        LogArguments = True
        LogReturnValue = True
        LogTimeStamp = True
        LogExecutionTime = False
    End Sub

    Public Property LogArguments As Boolean
    Public Property LogReturnValue As Boolean
    Public Property LogTimeStamp As Boolean
    Public Property LogExecutionTime As Boolean
    Public Property LogFilePath As String

    Friend Function Clone() As LogOptions
        Return New LogOptions With {
            .LogArguments = LogArguments,
            .LogReturnValue = LogReturnValue,
            .LogTimeStamp = LogTimeStamp,
            .LogExecutionTime = LogExecutionTime,
            .LogFilePath = LogFilePath
        }
    End Function
End Class

''' <summary>
''' Writes procedure entries to the configured log file and creates logging scopes
''' used by generated instrumentation.
''' </summary>
Public NotInheritable Class ProcedureLogger
    Private Const DefaultLogFileName As String = "Procedure.log"
    Private Shared ReadOnly _syncRoot As New Object()
    Private Shared _logFilePath As String

    Private Sub New()
    End Sub

    ''' <summary>
    ''' Gets or sets the log file path used when an invocation does not provide one.
    ''' Relative paths are resolved from the application's execution directory.
    ''' </summary>
    Public Shared Property LogFilePath As String
        Get
            SyncLock _syncRoot
                If String.IsNullOrWhiteSpace(_logFilePath) Then
                    Return GetDefaultLogFilePath()
                End If

                Return ResolvePath(_logFilePath)
            End SyncLock
        End Get
        Set(value As String)
            SyncLock _syncRoot
                _logFilePath = If(String.IsNullOrWhiteSpace(value), Nothing, value)
            End SyncLock
        End Set
    End Property

    ''' <summary>Returns the default Procedure.log path in the application directory.</summary>
    Public Shared Function GetDefaultLogFilePath() As String
        Return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultLogFileName)
    End Function

    ''' <summary>Configures the process-wide default log file path.</summary>
    Public Shared Sub Configure(Optional customLogFilePath As String = Nothing)
        LogFilePath = customLogFilePath
    End Sub

    ''' <summary>
    ''' Starts a logging scope and writes the procedure-entry line immediately.
    ''' </summary>
    Public Shared Function Begin(procedureName As String,
                                 arguments As Object(),
                                 Optional options As LogOptions = Nothing) As ProcedureLogScope
        Dim effectiveOptions As LogOptions = If(options Is Nothing, New LogOptions(), options.Clone())
        Return New ProcedureLogScope(procedureName:=procedureName,
                                     arguments:=arguments,
                                     options:=effectiveOptions)
    End Function

    ''' <summary>
    ''' Writes a single log line. This is useful for callers that do not need automatic
    ''' completion instrumentation.
    ''' </summary>
    Public Shared Sub Write(procedureName As String,
                            arguments As Object(),
                            Optional returnValue As Object = Nothing,
                            Optional options As LogOptions = Nothing)
        Dim scope As ProcedureLogScope = Begin(procedureName:=procedureName,
                                               arguments:=arguments,
                                               options:=options)
        scope.Complete(returnValue:=returnValue)
    End Sub

    ''' <summary>Formats a value for a human-readable procedure log.</summary>
    Public Shared Function FormatValue(value As Object) As String
        If value Is Nothing Then
            Return "Nothing"
        End If

        If TypeOf value Is String Then
            Return """" & DirectCast(value, String).Replace("""", """""") & """"
        End If

        If TypeOf value Is Char Then
            Return """" & DirectCast(value, Char).ToString().Replace("""", """""") & """"
        End If

        If TypeOf value Is Boolean Then
            Return DirectCast(value, Boolean).ToString()
        End If

        If TypeOf value Is Date Then
            Return DirectCast(value, Date).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        End If

        If TypeOf value Is IEnumerable AndAlso TypeOf value IsNot String Then
            Dim values As New List(Of String)()
            For Each item As Object In DirectCast(value, IEnumerable)
                values.Add(FormatValue(item))
            Next
            Return "[" & String.Join(", ", values.ToArray()) & "]"
        End If

        If TypeOf value Is IFormattable Then
            Return DirectCast(value, IFormattable).ToString(Nothing, CultureInfo.InvariantCulture)
        End If

        Return Convert.ToString(value, CultureInfo.InvariantCulture)
    End Function

    Friend Shared Function ResolvePath(filePath As String) As String
        If String.IsNullOrWhiteSpace(filePath) Then
            Return GetDefaultLogFilePath()
        End If

        If Path.IsPathRooted(filePath) Then
            Return filePath
        End If

        Return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath)
    End Function

    Friend Shared Sub WriteLine(filePath As String, line As String)
        Try
            Dim selectedPath As String = filePath
            If String.IsNullOrWhiteSpace(selectedPath) Then
                selectedPath = LogFilePath
            End If

            Dim fullPath As String = ResolvePath(filePath:=selectedPath)
            Dim directoryPath As String = Path.GetDirectoryName(fullPath)
            If Not String.IsNullOrWhiteSpace(directoryPath) Then
                Directory.CreateDirectory(directoryPath)
            End If

            SyncLock _syncRoot
                Using writer As New StreamWriter(fullPath, True, New UTF8Encoding(False))
                    writer.WriteLine(line)
                End Using
            End SyncLock
        Catch ex As Exception
            ' Logging must never change the behavior of the procedure being logged.
        End Try
    End Sub

    Friend Shared Function FormatPrefix(includeTimeStamp As Boolean) As String
        If Not includeTimeStamp Then
            Return String.Empty
        End If

        Return "[ " & Date.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & " ] "
    End Function
End Class

''' <summary>
''' Represents one instrumented procedure invocation.
''' </summary>
Public NotInheritable Class ProcedureLogScope
    Implements IDisposable

    Private ReadOnly _procedureName As String
    Private ReadOnly _arguments As Object()
    Private ReadOnly _options As LogOptions
    Private ReadOnly _started As Stopwatch
    Private _completed As Integer

    Friend Sub New(procedureName As String, arguments As Object(), options As LogOptions)
        _procedureName = If(procedureName, String.Empty)
        _arguments = If(arguments, New Object() {})
        _options = If(options, New LogOptions())
        _started = Stopwatch.StartNew()

        ProcedureLogger.WriteLine(filePath:=_options.LogFilePath,
                                  line:=ProcedureLogger.FormatPrefix(includeTimeStamp:=_options.LogTimeStamp) & FormatInvocation())
    End Sub

    ''' <summary>Completes a Sub procedure invocation.</summary>
    Public Sub Complete()
        CompleteCore(hasReturnValue:=False, returnValue:=Nothing)
    End Sub

    ''' <summary>Completes a Function procedure invocation and records its result.</summary>
    Public Sub Complete(returnValue As Object)
        CompleteCore(hasReturnValue:=True, returnValue:=returnValue)
    End Sub

    ''' <summary>
    ''' Records a function result and returns it, allowing generated code to preserve
    ''' a single evaluation of a Return expression.
    ''' </summary>
    Public Function CompleteAndReturn(Of T)(returnValue As T) As T
        Complete(returnValue:=returnValue)
        Return returnValue
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Complete()
    End Sub

    Private Sub CompleteCore(hasReturnValue As Boolean, returnValue As Object)
        If Interlocked.Exchange(_completed, 1) <> 0 Then
            Return
        End If

        _started.Stop()

        Dim shouldWrite As Boolean = (hasReturnValue AndAlso _options.LogReturnValue) OrElse _options.LogExecutionTime
        If Not shouldWrite Then
            Return
        End If

        Dim builder As New StringBuilder()
        builder.Append(ProcedureLogger.FormatPrefix(includeTimeStamp:=_options.LogTimeStamp))
        builder.Append(FormatInvocation())

        If hasReturnValue AndAlso _options.LogReturnValue Then
            builder.Append(" => ")
            builder.Append(ProcedureLogger.FormatValue(value:=returnValue))
        End If

        If _options.LogExecutionTime Then
            builder.Append(" (Execution time: ")
            builder.Append(_started.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture))
            builder.Append(" ms)")
        End If

        ProcedureLogger.WriteLine(filePath:=_options.LogFilePath, line:=builder.ToString())
    End Sub

    Private Function FormatInvocation() As String
        If Not _options.LogArguments Then
            Return _procedureName
        End If

        Dim values As New List(Of String)()
        For Each argument As Object In _arguments
            values.Add(ProcedureLogger.FormatValue(value:=argument))
        Next

        Return _procedureName & "(" & String.Join(", ", values.ToArray()) & ")"
    End Function
End Class
