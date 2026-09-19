Imports System
Imports System.Collections.Generic
Imports System.IO

Imports Microsoft.Build.Framework
Imports Microsoft.Build.Utilities
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

''' <summary>
''' Transforms Visual Basic source containing &lt;Log&gt; procedures into source that
''' calls the runtime logger at procedure entry and completion.
''' </summary>
Public NotInheritable Class SourceInstrumenter
    Private Sub New()
    End Sub

    Public Shared Function Instrument(source As String) As String
        If String.IsNullOrEmpty(source) OrElse source.IndexOf("<Log", StringComparison.OrdinalIgnoreCase) < 0 Then
            Return source
        End If

        Dim tree As SyntaxTree = VisualBasicSyntaxTree.ParseText(source)
        Dim rewriter As New LogMethodRewriter()
        Dim rewritten As SyntaxNode = rewriter.Visit(tree.GetRoot())
        Return rewritten.ToFullString()
    End Function

    Public Shared Sub InstrumentFile(inputPath As String, outputPath As String)
        Dim source As String = File.ReadAllText(inputPath)
        Dim transformed As String = Instrument(source)
        Dim outputDirectory As String = Path.GetDirectoryName(outputPath)
        If Not String.IsNullOrWhiteSpace(outputDirectory) Then
            Directory.CreateDirectory(outputDirectory)
        End If
        File.WriteAllText(outputPath, transformed)
    End Sub

    Private NotInheritable Class LogMethodRewriter
        Inherits VisualBasicSyntaxRewriter

        Public Overrides Function VisitMethodBlock(node As MethodBlockSyntax) As SyntaxNode
            Dim logAttribute As AttributeSyntax = FindLogAttribute(statement:=node.SubOrFunctionStatement)
            If logAttribute Is Nothing Then
                Return MyBase.VisitMethodBlock(node)
            End If

            Dim methodName As String = node.SubOrFunctionStatement.Identifier.ValueText
            If String.IsNullOrWhiteSpace(methodName) Then
                Return MyBase.VisitMethodBlock(node)
            End If

            Dim scopeName As String = GetAvailableName(node:=node, baseName:="__vbProcedureLoggerScope")
            Dim beginStatement As StatementSyntax = ParseStatement(
                source:=BuildBeginStatement(node:=node, logAttribute:=logAttribute, scopeName:=scopeName))
            Dim transformedStatements As New List(Of StatementSyntax) From {beginStatement}

            For Each statement As StatementSyntax In node.Statements
                Dim rewrittenStatement As SyntaxNode = New ReturnRewriter(scopeName:=scopeName).Visit(statement)
                transformedStatements.Add(DirectCast(rewrittenStatement, StatementSyntax))
            Next

            transformedStatements.Add(ParseStatement(source:=scopeName & ".Complete()"))
            Return node.WithStatements(SyntaxFactory.List(transformedStatements))
        End Function

        Private NotInheritable Class ReturnRewriter
            Inherits VisualBasicSyntaxRewriter

            Private ReadOnly _scopeName As String

            Public Sub New(scopeName As String)
                _scopeName = scopeName
            End Sub

            Public Overrides Function VisitReturnStatement(node As ReturnStatementSyntax) As SyntaxNode
                If node.Expression IsNot Nothing Then
                    Dim expression As ExpressionSyntax = SyntaxFactory.ParseExpression(
                        _scopeName & ".CompleteAndReturn(" & node.Expression.ToString() & ")")
                    Return node.WithExpression(expression)
                End If

                Dim earlyReturn As String = "If True Then" & Environment.NewLine &
                                            _scopeName & ".Complete()" & Environment.NewLine &
                                            "Return" & Environment.NewLine &
                                            "End If"
                Return SyntaxFactory.ParseExecutableStatement(earlyReturn).WithLeadingTrivia(
                    SyntaxFactory.EndOfLine(Environment.NewLine)).WithTrailingTrivia(
                    SyntaxFactory.EndOfLine(Environment.NewLine))
            End Function
        End Class

        Private Shared Function FindLogAttribute(statement As MethodStatementSyntax) As AttributeSyntax
            For Each attributeList As AttributeListSyntax In statement.AttributeLists
                For Each attribute As SyntaxNode In attributeList.Attributes
                    Dim name As String = DirectCast(attribute, AttributeSyntax).Name.ToString()
                    If name.Equals("Log", StringComparison.OrdinalIgnoreCase) OrElse
                       name.Equals("LogAttribute", StringComparison.OrdinalIgnoreCase) Then
                        Return DirectCast(attribute, AttributeSyntax)
                    End If
                Next
            Next
            Return Nothing
        End Function

        Private Shared Function BuildBeginStatement(node As MethodBlockSyntax,
                                                      logAttribute As AttributeSyntax,
                                                      scopeName As String) As String
            Dim methodName As String = node.SubOrFunctionStatement.Identifier.ValueText
            Dim argumentExpressions As New List(Of String)()

            If node.SubOrFunctionStatement.ParameterList IsNot Nothing Then
                For Each parameter As ParameterSyntax In node.SubOrFunctionStatement.ParameterList.Parameters
                    Dim parameterName As String = parameter.Identifier.Identifier.ValueText
                    If Not String.IsNullOrWhiteSpace(parameterName) Then
                        argumentExpressions.Add(parameterName)
                    End If
                Next
            End If

            Dim arguments As String = "Nothing"
            If argumentExpressions.Count > 0 Then
                arguments = "New Object() {" & String.Join(", ", argumentExpressions.ToArray()) & "}"
            End If

            Dim optionValues As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"LogArguments", "True"},
                {"LogReturnValue", "True"},
                {"LogTimeStamp", "True"},
                {"LogExecutionTime", "False"}
            }

            If logAttribute.ArgumentList IsNot Nothing Then
                For Each argument As SyntaxNode In logAttribute.ArgumentList.Arguments
                    Dim rawArgument As String = argument.ToString()
                    Dim separator As Integer = rawArgument.IndexOf(":=", StringComparison.Ordinal)
                    If separator < 0 Then
                        separator = rawArgument.IndexOf("=", StringComparison.Ordinal)
                    End If

                    If separator >= 0 Then
                        Dim optionName As String = rawArgument.Substring(0, separator).Trim()
                        Dim separatorLength As Integer = If(rawArgument.Substring(separator, 2) = ":=", 2, 1)
                        Dim optionValue As String = rawArgument.Substring(separator + separatorLength).Trim()
                        If optionValues.ContainsKey(optionName) Then
                            optionValues(optionName) = optionValue
                        End If
                    End If
                Next
            End If

            Dim options As String = "New Global.VBProcedureLogger.Runtime.LogOptions With {" &
                                    ".LogArguments = " & optionValues("LogArguments") & ", " &
                                    ".LogReturnValue = " & optionValues("LogReturnValue") & ", " &
                                    ".LogTimeStamp = " & optionValues("LogTimeStamp") & ", " &
                                    ".LogExecutionTime = " & optionValues("LogExecutionTime")

            Dim filePath As String = GetNamedArgument(attribute:=logAttribute, name:="LogFilePath")
            If filePath IsNot Nothing Then
                options &= ", .LogFilePath = " & filePath
            End If
            options &= "}"

            Return "Dim " & scopeName & " As Global.VBProcedureLogger.Runtime.ProcedureLogScope = " &
                   "Global.VBProcedureLogger.Runtime.ProcedureLogger.Begin(" &
                   "procedureName:=" & QuoteLiteral(methodName) & ", arguments:=" & arguments & ", options:=" & options & ")"
        End Function

        Private Shared Function GetNamedArgument(attribute As AttributeSyntax, name As String) As String
            If attribute.ArgumentList Is Nothing Then
                Return Nothing
            End If

            For Each argument As SyntaxNode In attribute.ArgumentList.Arguments
                Dim rawArgument As String = argument.ToString()
                Dim separator As Integer = rawArgument.IndexOf(":=", StringComparison.Ordinal)
                If separator < 0 Then
                    separator = rawArgument.IndexOf("=", StringComparison.Ordinal)
                End If

                If separator >= 0 AndAlso rawArgument.Substring(0, separator).Trim().Equals(name, StringComparison.OrdinalIgnoreCase) Then
                    Dim separatorLength As Integer = If(rawArgument.Substring(separator, 2) = ":=", 2, 1)
                    Return rawArgument.Substring(separator + separatorLength).Trim()
                End If
            Next
            Return Nothing
        End Function

        Private Shared Function GetAvailableName(node As MethodBlockSyntax, baseName As String) As String
            Dim candidate As String = baseName
            Dim suffix As Integer = 0
            Dim sourceText As String = node.ToString()
            While sourceText.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0
                suffix += 1
                candidate = baseName & suffix.ToString()
            End While
            Return candidate
        End Function

        Private Shared Function ParseStatement(source As String) As StatementSyntax
            Return SyntaxFactory.ParseExecutableStatement(source).WithLeadingTrivia(
                SyntaxFactory.EndOfLine(Environment.NewLine)).WithTrailingTrivia(
                SyntaxFactory.EndOfLine(Environment.NewLine))
        End Function

        Private Shared Function QuoteLiteral(value As String) As String
            Return """" & value.Replace("""", """""") & """"
        End Function
    End Class
End Class

''' <summary>
''' MSBuild task used by the NuGet build target to instrument the consumer's VB files
''' in an intermediate directory before the normal compiler target runs.
''' </summary>
Public Class InstrumentVisualBasicSources
    Inherits Task

    <Required>
    Public Property Sources As ITaskItem()

    <Required>
    Public Property IntermediateDirectory As String

    <Output>
    Public Property GeneratedSources As ITaskItem()

    Public Overrides Function Execute() As Boolean
        Try
            Dim output As New List(Of ITaskItem)()
            If Sources Is Nothing Then
                GeneratedSources = output.ToArray()
                Return True
            End If

            Dim root As String = Path.Combine(IntermediateDirectory, "VBProcedureLogger")
            Directory.CreateDirectory(root)

            Dim index As Integer = 0
            For Each source As ITaskItem In Sources
                Dim inputPath As String = source.ItemSpec
                Dim outputPath As String = Path.Combine(root, index.ToString("D5") & "_" & Path.GetFileName(inputPath))
                SourceInstrumenter.InstrumentFile(inputPath, outputPath)
                output.Add(New TaskItem(outputPath))
                index += 1
            Next

            GeneratedSources = output.ToArray()
            Return True
        Catch ex As Exception
            Log.LogErrorFromException(ex, True, True, Nothing)
            Return False
        End Try
    End Function
End Class
