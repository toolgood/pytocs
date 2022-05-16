#region License
//  Copyright 2015-2021 John Källén
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//      http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pytocs.Core.CodeModel;

namespace Pytocs.Core.Translate
{
    public class LocalVariableGenerator : ICodeStatementVisitor<int>
    {
        private Dictionary<CodeStatement, List<CodeStatement>> parentOf;
        private List<CodeStatement> path;
        private Dictionary<CodeVariableReferenceExpression, List<List<CodeStatement>>> paths;
        private List<CodeParameterDeclarationExpression> parameters;
        private List<CodeStatement> statements;
        private HashSet<string> globals;

        public static void Generate(CodeMemberMethod method, HashSet<string> globals)
        {
            var gen = new LocalVariableGenerator(method.Parameters, method.Statements, globals);
            gen.Analyze(new List<CodeStatement>(), method.Statements);
            gen.Generate();
        }

        public static void Generate(List<CodeParameterDeclarationExpression>? parameters, List<CodeStatement> statements, HashSet<string> globals)
        {
            parameters ??= new List<CodeParameterDeclarationExpression>();
            var gen = new LocalVariableGenerator(parameters, statements, globals);
            gen.Analyze(new List<CodeStatement>(), statements);
            gen.Generate();
        }

        private LocalVariableGenerator(
            List<CodeParameterDeclarationExpression> parameters,
            List<CodeStatement> statements,
            HashSet<string> globals)
        {
            this.parameters = parameters;
            this.statements = statements;
            this.globals = globals;
            this.parentOf = new Dictionary<CodeStatement, List<CodeStatement>>();
            this.path = new List<CodeStatement>();
            this.paths = new Dictionary<CodeVariableReferenceExpression, List<List<CodeStatement>>>(
                new IdCmp());
        }

        private void Analyze(List<CodeStatement> basePath, List<CodeStatement> statements)
        {
            foreach (var stm in statements)
            {
                var opath = this.path;
                this.path = basePath.Concat(new[] { stm }).ToList();
                stm.Accept(this);
                parentOf[stm] = statements;
                this.path = opath;
            }
        }

        private void AnalyzeExp(List<CodeStatement> basePath, CodeExpression exp)
        {
            var visitor = new ExpLocalVarGenerator(this);
            exp.Accept(visitor);
        }

        private void Generate()
        {
            var paramNames = new HashSet<string?>(
                parameters
                .Select(p => p.ParameterName));
            foreach (var de in paths.Where(
                d => !paramNames.Contains(d.Key.Name)))
            {
                var id = de.Key;
                var defs = de.Value;
                if (defs.Count == 1)
                {
                    ReplaceWithDeclaration(id, defs[0], defs[0].Count - 1);
                }
                else
                {
                    var first = defs[0];
                    int iCommon = first.Count - 1;
                    foreach (var path in defs.Skip(1))
                    {
                        iCommon = FindCommon(first, path, iCommon);
                    }
                    ReplaceWithDeclaration(id, first, iCommon);
                }
            }
        }

        private int FindCommon(List<CodeStatement> a, List<CodeStatement> b, int iCommon)
        {
            iCommon = Math.Min(iCommon, a.Count - 1);
            iCommon = Math.Min(iCommon, b.Count - 1);
            int i;
            for (i = 0; i <= iCommon; ++i)
            {
                if (parentOf[a[i]] != parentOf[b[i]])
                    return i - 1;
                if (a[i] != b[i])
                    return i;
            }
            return i;
        }

        private void ReplaceWithDeclaration(CodeVariableReferenceExpression id, List<CodeStatement> path, int iCommon)
        {
            if (iCommon >= 0)
            {
                var codeStatement = path[iCommon];
                var stms = this.parentOf[codeStatement];
                var i = stms.IndexOf(codeStatement);
                if (i >= 0)
                {
                    if (codeStatement is CodeAssignStatement ass)
                    {
                        // An assignment of type 'name = <exp>'. If the value
                        // assigned is null, use object type 'object', otherwise
                        // use 'var'.
                        var dstType = ass.Source is CodePrimitiveExpression prim && prim.Value == null
                            ? "object"
                            : "var";
                        stms[i] = new CodeVariableDeclarationStatement(dstType, id.Name)
                        {
                            InitExpression = ass.Source
                        };
                        return;
                    }
                }
            }
            statements.Insert(
                0,
                new CodeVariableDeclarationStatement("object", id.Name));
        }

        private void EnsurePath(CodeVariableReferenceExpression id)
        {
            if (!this.paths.TryGetValue(id, out var paths))
            {
                paths = new List<List<CodeStatement>>();
                this.paths.Add(id, paths);
            }
            paths.Add(this.path);
        }

        public int VisitAssignment(CodeAssignStatement ass)
        {
            if (ass.Destination is CodeVariableReferenceExpression id)
            {
                if (this.globals.Contains(id.Name))
                    return 0;
                EnsurePath(id);
                return 0;
            }
            return 0;
        }

        public int VisitBreak(CodeBreakStatement b)
        {
            return 0;
        }

        public int VisitComment(CodeCommentStatement codeCommentStatement)
        {
            return 0;
        }

        public int VisitContinue(CodeContinueStatement c)
        {
            return 0;
        }

        public int VisitForeach(CodeForeachStatement f)
        {
            AnalyzeExp(this.path, f.Collection);
            Analyze(this.path, f.Statements);
            return 0;
        }

        public int VisitIf(CodeConditionStatement cond)
        {
            AnalyzeExp(this.path, cond.Condition);
            Analyze(this.path, cond.TrueStatements);
            Analyze(this.path, cond.FalseStatements);
            return 0;
        }

        public int VisitLocalFunction(CodeLocalFunction _)
        {
            return 0;
        }

        public int VisitPostTestLoop(CodePostTestLoopStatement l)
        {
            AnalyzeExp(this.path, l.Test);
            Analyze(this.path, l.Body);
            return 0;
        }

        public int VisitPreTestLoop(CodePreTestLoopStatement l)
        {
            AnalyzeExp(this.path, l.Test);
            Analyze(this.path, l.Body);
            return 0;
        }

        public int VisitReturn(CodeMethodReturnStatement ret)
        {
            return 0;
        }

        public int VisitSideEffect(CodeExpressionStatement side)
        {
            return 0;
        }

        public int VisitThrow(CodeThrowExceptionStatement t)
        {
            return 0;
        }

        public int VisitTry(CodeTryCatchFinallyStatement t)
        {
            Analyze(this.path, t.TryStatements);
            foreach (var c in t.CatchClauses)
            {
                Analyze(this.path, c.Statements);
            }
            Analyze(this.path, t.FinallyStatements);
            return 0;
        }

        public int VisitUsing(CodeUsingStatement codeUsingStatement)
        {
            return 0;
        }

        public int VisitVariableDeclaration(CodeVariableDeclarationStatement decl)
        {
            return 0;
        }

        public int VisitYield(CodeYieldStatement y)
        {
            return 0;
        }

        internal class IdCmp : IEqualityComparer<CodeVariableReferenceExpression>
        {
            public bool Equals(CodeVariableReferenceExpression x, CodeVariableReferenceExpression y)
            {
                return x.Name == y.Name;
            }

            public int GetHashCode(CodeVariableReferenceExpression obj)
            {
                return obj.Name.GetHashCode();
            }
        }

        class ExpLocalVarGenerator : ICodeExpressionVisitor
        {
            private readonly LocalVariableGenerator outer;

            public ExpLocalVarGenerator(LocalVariableGenerator outer)
            {
                this.outer = outer;
            }

            public void VisitApplication(CodeApplicationExpression app)
            {
                foreach (var e in app.Arguments)
                {
                    e.Accept(this);
                }
            }

            public void VisitArrayIndexer(CodeArrayIndexerExpression aref)
            {
                throw new NotImplementedException();
            }

            public void VisitArrayInitializer(CodeArrayCreateExpression arr)
            {
                throw new NotImplementedException();
            }

            public void VisitAwait(CodeAwaitExpression awaitExp)
            {
                awaitExp.Expression.Accept(this);
            }

            public void VisitBase(CodeBaseReferenceExpression baseExp)
            {
                throw new NotImplementedException();
            }

            public void VisitBinary(CodeBinaryOperatorExpression bin)
            {
                if (bin.Operator == CodeOperatorType.Assign && bin.Left is CodeVariableReferenceExpression id)
                {
                    if (outer.globals.Contains(id.Name))
                        return;
                    outer.EnsurePath(id);
                }
                else
                {
                    bin.Left.Accept(this);
                }
                bin.Right.Accept(this);
            }

            public void VisitCast(CodeCastExpression cast)
            {
                throw new NotImplementedException();
            }

            public void VisitCollectionInitializer(CodeCollectionInitializer i)
            {
                throw new NotImplementedException();
            }

            public void VisitCondition(CodeConditionExpression condition)
            {
                throw new NotImplementedException();
            }

            public void VisitDefaultExpression(CodeDefaultExpression defaultExp)
            {
                throw new NotImplementedException();
            }

            public void VisitFieldReference(CodeFieldReferenceExpression field)
            {
                field.Expression.Accept(this);
            }

            public void VisitLambda(CodeLambdaExpression l)
            {
                throw new NotImplementedException();
            }

            public void VisitMethodReference(CodeMethodReferenceExpression m)
            {
                throw new NotImplementedException();
            }

            public void VisitNamedArgument(CodeNamedArgument arg)
            {
                throw new NotImplementedException();
            }

            public void VisitNumericLiteral(CodeNumericLiteral literal)
            {
            }

            public void VisitObjectCreation(CodeObjectCreateExpression c)
            {
                throw new NotImplementedException();
            }

            public void VisitObjectInitializer(CodeObjectInitializer i)
            {
                throw new NotImplementedException();
            }

            public void VisitParameterDeclaration(CodeParameterDeclarationExpression param)
            {
                param.DefaultValue?.Accept(this);
            }

            public void VisitPrimitive(CodePrimitiveExpression p)
            {
            }

            public void VisitQueryExpression(CodeQueryExpression q)
            {
                throw new NotImplementedException();
            }

            public void VisitThisReference(CodeThisReferenceExpression t)
            {
            }

            public void VisitTypeReference(CodeTypeReferenceExpression t)
            {
            }

            public void VisitUnary(CodeUnaryOperatorExpression u)
            {
                throw new NotImplementedException();
            }

            public void VisitValueTuple(CodeValueTupleExpression codeValueTupleExpression)
            {
                throw new NotImplementedException();
            }

            public void VisitVariableReference(CodeVariableReferenceExpression var)
            {
            }
        }
    }
}
