// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.PythonTools.Analysis.Infrastructure;
using Microsoft.PythonTools.Analysis.Values;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Analyzer {
    internal class DDG : PythonWalker {
        internal AnalysisUnit _unit;
        internal ExpressionEvaluator _eval;
        private SuiteStatement _curSuite;
        public readonly HashSet<ProjectEntry> AnalyzedEntries = new HashSet<ProjectEntry>();

        public void Analyze(Deque<AnalysisUnit> queue, CancellationToken cancel, Action<int> reportQueueSize = null, int reportQueueInterval = 1) {
            if (cancel.IsCancellationRequested) {
                return;
            }
            try {
                // Including a marker at the end of the queue allows us to see in
                // the log how frequently the queue empties.
                var endOfQueueMarker = new AnalysisUnit(null, null);
                int queueCountAtStart = queue.Count;
                int reportInterval = reportQueueInterval - 1;

                if (queueCountAtStart > 0) {
                    queue.Append(endOfQueueMarker);
                }

                while (queue.Count > 0 && !cancel.IsCancellationRequested) {
                    _unit = queue.PopLeft();

                    if (_unit == endOfQueueMarker) {
                        AnalysisLog.EndOfQueue(queueCountAtStart, queue.Count);
                        if (reportInterval < 0 && reportQueueSize != null) {
                            reportQueueSize(queue.Count);
                        }

                        queueCountAtStart = queue.Count;
                        if (queueCountAtStart > 0) {
                            queue.Append(endOfQueueMarker);
                        }
                        continue;
                    }

                    AnalysisLog.Dequeue(queue, _unit);
                    if (reportInterval == 0 && reportQueueSize != null) {
                        reportQueueSize(queue.Count);
                        reportInterval = reportQueueInterval - 1;
                    } else if (reportInterval > 0) {
                        reportInterval -= 1;
                    }

                    _unit.IsInQueue = false;
                    SetCurrentUnit(_unit);
                    AnalyzedEntries.Add(_unit.ProjectEntry);
                    _unit.Analyze(this, cancel);
                }

                if (reportQueueSize != null) {
                    reportQueueSize(0);
                }

                if (cancel.IsCancellationRequested) {
                    AnalysisLog.Cancelled(queue);
                }
            } finally {
                AnalysisLog.Flush();
                AnalyzedEntries.Remove(null);
            }
        }

        public void SetCurrentUnit(AnalysisUnit unit) {
            _eval = new ExpressionEvaluator(unit);
            _unit = unit;
        }

        public InterpreterScope Scope {
            get {
                return _eval.Scope;
            }
            set {
                _eval.Scope = value;
            }
        }

        public ModuleInfo GlobalScope {
            get { return _unit.DeclaringModule; }
        }

        public PythonAnalyzer ProjectState {
            get { return _unit.State; }
        }

        public override bool Walk(PythonAst node) {
            ModuleReference existingRef;
            Debug.Assert(node == _unit.Ast);

            if (!ProjectState.Modules.TryImport(_unit.DeclaringModule.Name, out existingRef)) {
                // publish our module ref now so that we don't collect dependencies as we'll be fully processed
                if (existingRef == null) {
                    ProjectState.Modules[_unit.DeclaringModule.Name] = new ModuleReference(_unit.DeclaringModule, _unit.DeclaringModule.Name);
                } else {
                    existingRef.Module = _unit.DeclaringModule;
                }
            }

            return base.Walk(node);
        }

        /// <summary>
        /// Gets the function which we are processing code for currently or
        /// null if we are not inside of a function body.
        /// </summary>
        public FunctionScope CurrentFunction {
            get { return CurrentContainer<FunctionScope>(); }
        }

        public ClassScope CurrentClass {
            get { return CurrentContainer<ClassScope>(); }
        }

        private T CurrentContainer<T>() where T : InterpreterScope {
            return Scope.EnumerateTowardsGlobal.OfType<T>().FirstOrDefault();
        }

        public override bool Walk(AssignmentStatement node) {
            var valueType = _eval.Evaluate(node.Right);

            // For self assignments (e.g. "fob = fob"), include values from 
            // outer scopes, otherwise such assignments will always be unknown
            // because we use the unassigned variable for the RHS.
            var ne = node.Right as NameExpression;
            InterpreterScope oldScope;
            if (ne != null &&
                (oldScope = _eval.Scope).OuterScope != null &&
                (node.Left.OfType<NameExpression>().Any(n => n.Name == ne.Name) ||
                node.Left.OfType<ExpressionWithAnnotation>().Select(e => e.Expression).OfType<NameExpression>().Any(n => n.Name == ne.Name))) {
                try {
                    _eval.Scope = _eval.Scope.OuterScope;
                    valueType = valueType.Union(_eval.Evaluate(node.Right));
                } finally {
                    _eval.Scope = oldScope;
                }
            }

            foreach (var left in node.Left) {
                if (left is ExpressionWithAnnotation annoExpr && annoExpr.Annotation != null) {
                    var annoType = _eval.EvaluateAnnotation(annoExpr.Annotation);
                    if (annoType?.Any() == true) {
                        _eval.AssignTo(node, annoExpr.Expression, annoType);
                    }
                }

                _eval.AssignTo(node, left, valueType);
            }
            return false;
        }

        public override bool Walk(AugmentedAssignStatement node) {
            var right = _eval.Evaluate(node.Right);

            foreach (var x in _eval.Evaluate(node.Left)) {
                x.AugmentAssign(node, _unit, right);
            }
            return false;
        }

        public override bool Walk(GlobalStatement node) {
            foreach (var name in node.Names) {
                GlobalScope.Scope.GetVariable(name, _unit, name.Name);
            }
            return false;
        }

        public override bool Walk(NonlocalStatement node) {
            if (Scope.OuterScope != null) {
                foreach (var name in node.Names) {
                    Scope.OuterScope.GetVariable(name, _unit, name.Name);
                }
            }
            return false;
        }

        public override bool Walk(ClassDefinition node) {
            // Evaluate decorators for references
            // TODO: Should apply decorators when assigning the class
            foreach (var d in (node.Decorators?.DecoratorsInternal).MaybeEnumerate()) {
                _eval.Evaluate(d);
            }

            return false;
        }

        public override bool Walk(ExpressionStatement node) {
            _eval.Evaluate(node.Expression);

            if (node.Expression is ExpressionWithAnnotation annoExpr && annoExpr.Annotation != null) {
                // The variable is technically unassigned. However, other engines do show completion
                // on annotated, but not assigned variables. See https://github.com/Microsoft/PTVS/issues/3608
                // Pylint does not flag 'name' as unassigned in
                //
                //  class Employee(NamedTuple):
                //      name: str
                //      id: int = 3
                //
                //  employee = Employee('Guido')
                //  print(employee.name)
                var annoType = _eval.EvaluateAnnotation(annoExpr.Annotation);
                if (annoType?.Any() == true) {
                    _eval.AssignTo(node, annoExpr.Expression, annoType);
                }
            }
            return false;
        }

        public override bool Walk(ForStatement node) {
            if (node.List != null) {
                var list = _eval.Evaluate(node.List);
                _eval.AssignTo(
                    node,
                    node.Left,
                    node.IsAsync ? list.GetAsyncEnumeratorTypes(node, _unit) : list.GetEnumeratorTypes(node, _unit)
                );
            }

            if (node.Body != null) {
                node.Body.Walk(this);
            }

            if (node.Else != null) {
                node.Else.Walk(this);
            }
            return false;
        }

        private bool AssignImportedMember(Node node, IModule userMod, string[] attributes, string assignName) {
            if (attributes == null) {
                throw new ArgumentNullException(nameof(attributes));
            } else if (attributes.Length == 0) {
                throw new ArgumentException("non-empty attributes required", nameof(attributes));
            }
            return AssignImportedModuleOrMember(node, userMod, null, attributes, assignName, attributes?.Length == 1);
        }

        private bool AssignImportedModule(Node node, ModuleReference modRef, IReadOnlyList<string> attributes, string assignName) {
            return AssignImportedModuleOrMember(node, modRef.Module, modRef.AnalysisModule, attributes, assignName, false);
        }

        private bool AssignImportedModuleOrMember(Node node, IModule userMod, IAnalysisSet analysisMod, IReadOnlyList<string> attributes, string assignName, bool addLink) {
            var variable = Scope.CreateVariable(node, _unit, assignName, false);
            if (userMod == null) {
                return false;
            }

            bool added = false;
            if (attributes == null) {
                if (analysisMod != null) {
                    added = variable.AddTypes(_unit, analysisMod);
                }
            } else {
                var value = userMod.GetModuleMember(node, _unit, attributes[0], true, Scope, addLink ? assignName : null);

                foreach (var n in attributes.Skip(1)) {
                    value = value.GetMember(node, _unit, n);
                }

                added = variable.AddTypes(_unit, value);
            }

            if (added) {
                // anyone who read from the module will now need to get the new values
                GlobalScope.ModuleDefinition.EnqueueDependents();
            }

            return added;
        }

        public override bool Walk(FromImportStatement node) {
            var modName = node.Root.MakeString();

            if (!TryImportModule(modName, node.ForceAbsolute, out var modRef, out var bits)) {
                _unit.DeclaringModule.AddUnresolvedModule(modName, node.ForceAbsolute);
                return false;
            }

            _unit.DeclaringModule.AddModuleReference(modRef);

            Debug.Assert(modRef.Module != null);
            var userMod = modRef.Module;

            string[] fullImpName;
            if (bits != null) {
                fullImpName = new string[bits.Count + 1];
                bits.ToArray().CopyTo(fullImpName, 0);
            } else {
                fullImpName = new string[1];
            }

            var asNames = node.AsNames ?? node.Names;

            int len = Math.Min(node.Names.Count, asNames.Count);
            for (int i = 0; i < len; i++) {
                var nameNode = asNames[i] ?? node.Names[i];
                var impName = node.Names[i].Name;
                var newName = asNames[i] != null ? asNames[i].Name : null;

                if (string.IsNullOrEmpty(impName)) {
                    // incomplete import statement
                    continue;
                } else if (impName == "*") {
                    // Handle "import *"
                    if (userMod != null) {
                        userMod.Imported(_unit);

                        foreach (var varName in userMod.GetModuleMemberNames(GlobalScope.InterpreterContext)) {
                            if (!varName.StartsWithOrdinal("_")) {
                                fullImpName[fullImpName.Length - 1] = varName;
                                AssignImportedMember(nameNode, userMod, fullImpName, varName);
                            }
                        }
                    }
                } else {
                    userMod.Imported(_unit);
                    fullImpName[fullImpName.Length - 1] = impName;
                    AssignImportedMember(nameNode, userMod, fullImpName, newName ?? impName);
                }
            }

            return false;
        }

        private bool TryImportModule(string modName, bool forceAbsolute, out ModuleReference moduleRef, out IReadOnlyList<string> remainingParts) {
            moduleRef = null;
            remainingParts = null;

            if (ProjectState.Limits.CrossModule > 0 &&
                ProjectState.ModulesByFilename.Count > ProjectState.Limits.CrossModule) {
                // too many modules loaded, disable cross module analysis by blocking
                // scripts from seeing other modules.
                return false;
            }

            var candidates = PythonAnalyzer.ResolvePotentialModuleNames(_unit.ProjectEntry, modName, forceAbsolute).ToArray();
            foreach (var name in candidates) {
                if (ProjectState.Modules.TryImport(name, out moduleRef)) {
                    return true;
                }
            }

            foreach (var name in candidates) {
                moduleRef = null;
                foreach (var part in ModulePath.GetParents(name, includeFullName: true)) {
                    if (ProjectState.Modules.TryImport(part, out var mref)) {
                        moduleRef = mref;
                        if (part.Length < name.Length) {
                            moduleRef.Module?.Imported(_unit);
                        }
                    } else if (moduleRef != null) {
                        Debug.Assert(moduleRef.Name.Length + 1 < name.Length, $"Expected {name} to be a child of {moduleRef.Name}");
                        if (moduleRef.Name.Length + 1 < name.Length) {
                            remainingParts = name.Substring(moduleRef.Name.Length + 1).Split('.');
                        }
                        return true;
                    } else {
                        break;
                    }
                }
            }

            return moduleRef?.Module != null;
        }

        internal List<AnalysisValue> LookupBaseMethods(string name, IEnumerable<IAnalysisSet> bases, Node node, AnalysisUnit unit) {
            var result = new List<AnalysisValue>();
            foreach (var b in bases) {
                foreach (var curType in b) {
                    BuiltinClassInfo klass = curType as BuiltinClassInfo;
                    if (klass != null) {
                        var value = klass.GetMember(node, unit, name);
                        if (value != null) {
                            result.AddRange(value);
                        }
                    }
                }
            }
            return result;
        }


        public override bool Walk(FunctionDefinition node) {
            InterpreterScope funcScope;
            if (_unit.Scope.TryGetNodeScope(node, out funcScope)) {
                var function = ((FunctionScope)funcScope).Function;
                var analysisUnit = (FunctionAnalysisUnit)((FunctionScope)funcScope).Function.AnalysisUnit;

                var curClass = Scope as ClassScope;
                if (curClass != null) {
                    var bases = LookupBaseMethods(
                        analysisUnit.Ast.Name,
                        curClass.Class.Mro,
                        analysisUnit.Ast,
                        analysisUnit
                    );
                    foreach (var method in bases.OfType<BuiltinMethodInfo>()) {
                        foreach (var overload in method.Function.Overloads) {
                            function.UpdateDefaultParameters(_unit, overload.GetParameters());
                        }
                    }
                }
            }

            return false;
        }

        internal void WalkBody(Node node, AnalysisUnit unit) {
            var oldUnit = _unit;
            var eval = _eval;
            _unit = unit;
            _eval = new ExpressionEvaluator(unit);
            try {
                node.Walk(this);
            } finally {
                _unit = oldUnit;
                _eval = eval;
            }
        }

        public override bool Walk(IfStatement node) {
            foreach (var test in node.TestsInternal) {
                _eval.Evaluate(test.Test);

                var prevScope = Scope;

                TryPushIsInstanceScope(test, test.Test);

                test.Body.Walk(this);

                Scope = prevScope;
            }
            if (node.ElseStatement != null) {
                node.ElseStatement.Walk(this);
            }
            return false;
        }

        public override bool Walk(ImportStatement node) {
            int len = Math.Min(node.Names.Count, node.AsNames.Count);
            for (int i = 0; i < len; i++) {
                var curName = node.Names[i];
                var asName = node.AsNames[i];

                string importing, saveName;
                NameExpression nameNode;
                if (curName.Names.Count == 0) {
                    continue;
                } else if (curName.Names.Count > 1) {
                    // import fob.oar
                    if (asName != null) {
                        // import fob.oar as baz, baz becomes the value of the oar module
                        importing = curName.MakeString();
                        saveName = asName.Name;
                        nameNode = asName;
                    } else {
                        // plain import fob.oar, we bring in fob into the scope
                        saveName = importing = curName.Names[0].Name;
                        nameNode = curName.Names[0];
                    }
                } else {
                    // import fob
                    importing = curName.Names[0].Name;
                    if (asName != null) {
                        saveName = asName.Name;
                        nameNode = asName;
                    } else {
                        saveName = importing;
                        nameNode = curName.Names[0];
                    }
                }

                // Ensure a variable exists, even if the import fails
                Scope.CreateVariable(nameNode, _unit, saveName);

                if (!TryImportModule(importing, node.ForceAbsolute, out var modRef, out var bits)) {
                    _unit.DeclaringModule.AddUnresolvedModule(importing, node.ForceAbsolute);
                    continue;
                }

                _unit.DeclaringModule.AddModuleReference(modRef);

                var userMod = modRef.Module;
                Debug.Assert(userMod != null);

                if (userMod != null) {
                    userMod.Imported(_unit);

                    AssignImportedModule(nameNode, modRef, bits, saveName);
                }
            }
            return true;
        }

        public override bool Walk(ReturnStatement node) {
            var fnScope = CurrentFunction;
            if (node.Expression != null && fnScope != null) {
                var lookupRes = _eval.Evaluate(node.Expression);
                fnScope.AddReturnTypes(node, _unit, lookupRes);
            }
            return true;
        }

        public override bool Walk(WithStatement node) {
            foreach (var item in node.Items) {
                var ctxMgr = _eval.Evaluate(item.ContextManager);
                var enter = ctxMgr.GetMember(node, _unit, node.IsAsync ? "__aenter__" : "__enter__");
                var exit = ctxMgr.GetMember(node, _unit, node.IsAsync ? "__aexit__" : "__exit__");
                var ctxt = enter.Call(node, _unit, ExpressionEvaluator.EmptySets, ExpressionEvaluator.EmptyNames).Resolve(_unit);
                var exitRes = exit.Call(node, _unit, ExpressionEvaluator.EmptySets, ExpressionEvaluator.EmptyNames).Resolve(_unit);
                if (node.IsAsync) {
                    ctxt = ctxt.Await(node, _unit);
                    exitRes.Await(node, _unit);
                }
                if (item.Variable != null) {
                    _eval.AssignTo(node, item.Variable, ctxt);
                }
            }

            return true;
        }

        public override bool Walk(PrintStatement node) {
            foreach (var expr in node.Expressions) {
                _eval.Evaluate(expr);
            }
            return false;
        }

        public override bool Walk(AssertStatement node) {
            TryPushIsInstanceScope(node, node.Test);

            _eval.EvaluateMaybeNull(node.Test);
            _eval.EvaluateMaybeNull(node.Message);
            return false;
        }

        private void TryPushIsInstanceScope(Node node, Expression test) {
            InterpreterScope newScope;
            if (Scope.TryGetNodeScope(node, out newScope)) {
                var outerScope = Scope;
                var isInstanceScope = (IsInstanceScope)newScope;

                // magic assert isinstance statement alters the type information for a node
                var namesAndExpressions = OverviewWalker.GetIsInstanceNamesAndExpressions(test);
                foreach (var nameAndExpr in namesAndExpressions) {
                    var name = nameAndExpr.Key;
                    var type = nameAndExpr.Value;

                    var typeObj = _eval.EvaluateMaybeNull(type);
                    isInstanceScope.CreateTypedVariable(name, _unit, name.Name, typeObj);
                }

                // push the scope, it will be popped when we leave the current SuiteStatement.
                Scope = newScope;
            }
        }

        public override bool Walk(SuiteStatement node) {
            var prevSuite = _curSuite;
            var prevScope = Scope;

            _curSuite = node;
            if (node.Statements != null) {
                foreach (var statement in node.Statements) {
                    statement.Walk(this);
                }
            }

            Scope = prevScope;
            _curSuite = prevSuite;
            return false;
        }

        public override bool Walk(DelStatement node) {
            foreach (var expr in node.Expressions) {
                DeleteExpression(expr);
            }
            return false;
        }

        private void DeleteExpression(Expression expr) {
            NameExpression name = expr as NameExpression;
            if (name != null) {
                var var = Scope.CreateVariable(name, _unit, name.Name);

                return;
            }

            IndexExpression index = expr as IndexExpression;
            if (index != null) {
                var values = _eval.Evaluate(index.Target);
                var indexValues = _eval.Evaluate(index.Index);
                foreach (var value in values) {
                    value.DeleteIndex(index, _unit, indexValues);
                }
                return;
            }

            MemberExpression member = expr as MemberExpression;
            if (member != null) {
                if (!string.IsNullOrEmpty(member.Name)) {
                    var values = _eval.Evaluate(member.Target);
                    foreach (var value in values) {
                        value.DeleteMember(member, _unit, member.Name);
                    }
                }
                return;
            }

            ParenthesisExpression paren = expr as ParenthesisExpression;
            if (paren != null) {
                DeleteExpression(paren.Expression);
                return;
            }

            SequenceExpression seq = expr as SequenceExpression;
            if (seq != null) {
                foreach (var item in seq.Items) {
                    DeleteExpression(item);
                }
                return;
            }
        }

        public override bool Walk(RaiseStatement node) {
            _eval.EvaluateMaybeNull(node.Value);
            _eval.EvaluateMaybeNull(node.Traceback);
            _eval.EvaluateMaybeNull(node.ExceptType);
            _eval.EvaluateMaybeNull(node.Cause);
            return false;
        }

        public override bool Walk(WhileStatement node) {
            _eval.Evaluate(node.Test);

            node.Body.Walk(this);
            if (node.ElseStatement != null) {
                node.ElseStatement.Walk(this);
            }

            return false;
        }

        public override bool Walk(TryStatement node) {
            node.Body.Walk(this);
            if (node.Handlers != null) {
                foreach (var handler in node.Handlers) {
                    var test = AnalysisSet.Empty;
                    if (handler.Test != null) {
                        var testTypes = _eval.Evaluate(handler.Test);

                        if (handler.Target != null) {
                            foreach (var type in testTypes) {
                                ClassInfo klass = type as ClassInfo;
                                if (klass != null) {
                                    test = test.Union(klass.Instance.SelfSet);
                                }

                                BuiltinClassInfo builtinClass = type as BuiltinClassInfo;
                                if (builtinClass != null) {
                                    test = test.Union(builtinClass.Instance.SelfSet);
                                }
                            }

                            _eval.AssignTo(handler, handler.Target, test);
                        }
                    }

                    handler.Body.Walk(this);
                }
            }

            if (node.Finally != null) {
                node.Finally.Walk(this);
            }

            if (node.Else != null) {
                node.Else.Walk(this);
            }

            return false;
        }

        public override bool Walk(ExecStatement node) {
            if (node.Code != null) {
                _eval.Evaluate(node.Code);
            }
            if (node.Locals != null) {
                _eval.Evaluate(node.Locals);
            }
            if (node.Globals != null) {
                _eval.Evaluate(node.Globals);
            }
            return false;
        }
    }
}
