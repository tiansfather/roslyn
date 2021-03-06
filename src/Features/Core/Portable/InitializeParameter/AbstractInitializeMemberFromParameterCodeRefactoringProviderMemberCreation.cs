﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.Diagnostics.Analyzers.NamingStyles;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Semantics;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.InitializeParameter
{
    internal abstract partial class AbstractInitializeMemberFromParameterCodeRefactoringProvider<
        TParameterSyntax,
        TMemberDeclarationSyntax,
        TStatementSyntax,
        TExpressionSyntax,
        TBinaryExpressionSyntax> : AbstractInitializeParameterCodeRefactoringProvider<
            TParameterSyntax,
            TMemberDeclarationSyntax,
            TStatementSyntax,
            TExpressionSyntax,
            TBinaryExpressionSyntax>
        where TParameterSyntax : SyntaxNode
        where TMemberDeclarationSyntax : SyntaxNode
        where TStatementSyntax : SyntaxNode
        where TExpressionSyntax : SyntaxNode
        where TBinaryExpressionSyntax : TExpressionSyntax
    {
        // Standard field/property names we look for when we have a parameter with a given name.
        // We also use the rules to help generate fresh fields/properties.  Note that we always
        // look at these rules *after* the user's own rules.  That way we respect user naming, but
        // also have a reasonably fallback if they don't have any specified preferences.
        private static readonly ImmutableArray<NamingRule> s_builtInRules = ImmutableArray.Create(
                new NamingRule(new SymbolSpecification(
                    Guid.NewGuid(), "Property",
                    ImmutableArray.Create(new SymbolSpecification.SymbolKindOrTypeKind(SymbolKind.Property))),
                    new NamingStyles.NamingStyle(Guid.NewGuid(), capitalizationScheme: Capitalization.PascalCase),
                    enforcementLevel: DiagnosticSeverity.Hidden),
                new NamingRule(new SymbolSpecification(
                    Guid.NewGuid(), "Field",
                    ImmutableArray.Create(new SymbolSpecification.SymbolKindOrTypeKind(SymbolKind.Field))),
                    new NamingStyles.NamingStyle(Guid.NewGuid(), capitalizationScheme: Capitalization.CamelCase),
                    enforcementLevel: DiagnosticSeverity.Hidden),
                new NamingRule(new SymbolSpecification(
                    Guid.NewGuid(), "FieldWithUnderscore",
                    ImmutableArray.Create(new SymbolSpecification.SymbolKindOrTypeKind(SymbolKind.Field))),
                    new NamingStyles.NamingStyle(Guid.NewGuid(), prefix: "_", capitalizationScheme: Capitalization.CamelCase),
                    enforcementLevel: DiagnosticSeverity.Hidden));

        protected abstract SyntaxNode TryGetLastStatement(IBlockStatement blockStatementOpt);

        protected override async Task<ImmutableArray<CodeAction>> GetRefactoringsAsync(
            Document document, IParameterSymbol parameter, TMemberDeclarationSyntax memberDeclaration,
            IBlockStatement blockStatementOpt, CancellationToken cancellationToken)
        {
            // Only supported for constructor parameters.
            var methodSymbol = parameter.ContainingSymbol as IMethodSymbol;
            if (methodSymbol?.MethodKind != MethodKind.Constructor)
            {
                return ImmutableArray<CodeAction>.Empty;
            }

            var assignmentStatement = TryFindFieldOrPropertyAssignmentStatement(
                parameter, blockStatementOpt);
            if (assignmentStatement != null)
            {
                // We're already assigning this parameter to a field/property in this type.
                // So there's nothing more for us to do.
                return ImmutableArray<CodeAction>.Empty;
            }

            // Haven't initialized any fields/properties with this parameter.  Offer to assign
            // to an existing matching field/prop if we can find one, or add a new field/prop
            // if we can't.

            var fieldOrProperty = await TryFindMatchingUninitializedFieldOrPropertySymbolAsync(
                document, parameter, blockStatementOpt, cancellationToken).ConfigureAwait(false);

            if (fieldOrProperty != null)
            {
                // Found a field/property that this parameter should be assigned to.
                // Just offer the simple assignment to it.

                var resource = fieldOrProperty.Kind == SymbolKind.Field
                    ? FeaturesResources.Initialize_field_0
                    : FeaturesResources.Initialize_property_0;

                var title = string.Format(resource, fieldOrProperty.Name);

                return ImmutableArray.Create<CodeAction>(new MyCodeAction(
                    title,
                    c => AddSymbolInitializationAsync(
                        document, parameter, memberDeclaration, blockStatementOpt, fieldOrProperty, c)));
            }
            else
            {
                // Didn't find a field/prop that this parameter could be assigned to.
                // Offer to create new one and assign to that.
                var codeGenService = document.GetLanguageService<ICodeGenerationService>();

                // Get the parts of the parameter name and the appropriate naming rules so
                // that we can name the field/property accordingly.
                var parameterNameParts = this.GetParameterWordParts(parameter);
                var rules = await this.GetNamingRulesAsync(document, cancellationToken).ConfigureAwait(false);

                var field = CreateField(parameter, rules, parameterNameParts);
                var property = CreateProperty(parameter, rules, parameterNameParts);

                // Offer to generate either a property or a field.  Currently we place the property
                // suggestion first (to help users with the immutable object+property pattern). But
                // we could consider swapping this if people prefer creating private fields more.
                return ImmutableArray.Create<CodeAction>(
                    new MyCodeAction(string.Format(FeaturesResources.Create_and_initialize_property_0, property.Name),
                        c => AddSymbolInitializationAsync(document, parameter, memberDeclaration, blockStatementOpt, property, c)),
                    new MyCodeAction(string.Format(FeaturesResources.Create_and_initialize_field_0, field.Name),
                        c => AddSymbolInitializationAsync(document, parameter, memberDeclaration, blockStatementOpt, field, c)));
            }
        }

        private IFieldSymbol CreateField(
            IParameterSymbol parameter, ImmutableArray<NamingRule> rules, ImmutableArray<string> parameterNameParts)
        {
            foreach (var rule in rules)
            {
                if (rule.SymbolSpecification.AppliesTo(SymbolKind.Field, Accessibility.Private))
                {
                    var uniqueName = GenerateUniqueName(parameter, parameterNameParts, rule);

                    return CodeGenerationSymbolFactory.CreateFieldSymbol(
                        default,
                        Accessibility.Private,
                        DeclarationModifiers.ReadOnly,
                        parameter.Type, uniqueName);
                }
            }

            // We place a special rule in s_builtInRules that matches all fields.  So we should 
            // always find a matching rule.
            throw ExceptionUtilities.Unreachable;
        }

        private static string GenerateUniqueName(IParameterSymbol parameter, ImmutableArray<string> parameterNameParts, NamingRule rule)
        {
            // Determine an appropriate name to call the new field.
            var containingType = parameter.ContainingType;
            var baseName = rule.NamingStyle.CreateName(parameterNameParts);

            // Ensure that the name is unique in the containing type so we
            // don't stomp on an existing member.
            var uniqueName = NameGenerator.GenerateUniqueName(
                baseName, n => containingType.GetMembers(n).IsEmpty);
            return uniqueName;
        }

        private IPropertySymbol CreateProperty(
            IParameterSymbol parameter, ImmutableArray<NamingRule> rules, ImmutableArray<string> parameterNameParts)
        {
            foreach (var rule in rules)
            {
                if (rule.SymbolSpecification.AppliesTo(SymbolKind.Property, Accessibility.Public))
                {
                    var uniqueName = GenerateUniqueName(parameter, parameterNameParts, rule);

                    var getMethod = CodeGenerationSymbolFactory.CreateAccessorSymbol(
                        default,
                        Accessibility.Public,
                        default);

                    return CodeGenerationSymbolFactory.CreatePropertySymbol(
                        default,
                        Accessibility.Public,
                        new DeclarationModifiers(),
                        parameter.Type,
                        RefKind.None,
                        explicitInterfaceImplementations: default,
                        name: uniqueName,
                        parameters: default,
                        getMethod: getMethod,
                        setMethod: null);
                }
            }

            // We place a special rule in s_builtInRules that matches all properties.  So we should 
            // always find a matching rule.
            throw ExceptionUtilities.Unreachable;
        }

        private async Task<Document> AddSymbolInitializationAsync(
            Document document, IParameterSymbol parameter, TMemberDeclarationSyntax memberDeclaration,
            IBlockStatement blockStatementOpt, ISymbol fieldOrProperty, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var workspace = document.Project.Solution.Workspace;
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var editor = new SyntaxEditor(root, workspace);
            var generator = editor.Generator;

            if (fieldOrProperty.ContainingType == null)
            {
                // We're generating a new field/property.  Place into the containing type,
                // ideally before/after a relevant existing member.

                // First, look for the right containing type (As a type may be partial). 
                // We want the type-block that this constructor is contained within.
                var typeDeclaration = 
                    parameter.ContainingType.DeclaringSyntaxReferences
                                            .Select(r => GetTypeBlock(r.GetSyntax(cancellationToken)))
                                            .Single(d => memberDeclaration.Ancestors().Contains(d));

                // Now add the field/property to this type.  Use the 'ReplaceNode+callback' form
                // so that nodes will be appropriate tracked and so we can then update the constructor
                // below even after we've replaced the whole type with a new type.
                //
                // Note: We'll pass the appropriate options so that the new field/property 
                // is appropriate placed before/after an existing field/property.  We'll try
                // to preserve the same order for fields/properties that we have for the constructor
                // parameters.
                editor.ReplaceNode(
                    typeDeclaration,
                    (currentTypeDecl, _) =>
                    {
                        if (fieldOrProperty is IPropertySymbol property)
                        {
                            return CodeGenerator.AddPropertyDeclaration(
                                currentTypeDecl, property, workspace,
                                GetAddOptions<IPropertySymbol>(parameter, blockStatementOpt, typeDeclaration, cancellationToken));
                        }
                        else if (fieldOrProperty is IFieldSymbol field)
                        {
                            return CodeGenerator.AddFieldDeclaration(
                                currentTypeDecl, field, workspace,
                                GetAddOptions<IFieldSymbol>(parameter, blockStatementOpt, typeDeclaration, cancellationToken));
                        }
                        else
                        {
                            throw ExceptionUtilities.Unreachable;
                        }
                    });
            }

            // Now that we've added any potential members, create an assignment between it
            // and the parameter.
            var initializationStatement = (TStatementSyntax)generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.MemberAccessExpression(
                        generator.ThisExpression(),
                        generator.IdentifierName(fieldOrProperty.Name)),
                    generator.IdentifierName(parameter.Name)));

            // Attempt to place the initialization in a good location in the constructor
            // We'll want to keep initialization statements in the same order as we see
            // parameters for the constructor.
            var statementToAddAfterOpt = TryGetStatementToAddInitializationAfter(
                semanticModel, parameter, blockStatementOpt, cancellationToken);

            InsertStatement(editor, memberDeclaration, statementToAddAfterOpt, initializationStatement);

            return document.WithSyntaxRoot(editor.GetChangedRoot());
        }

        private CodeGenerationOptions GetAddOptions<TSymbol>(
            IParameterSymbol parameter, IBlockStatement blockStatementOpt,
            SyntaxNode typeDeclaration, CancellationToken cancellationToken)
            where TSymbol : ISymbol
        {
            var methodSymbol = (IMethodSymbol)parameter.ContainingSymbol;
            var parameterIndex = methodSymbol.Parameters.IndexOf(parameter);

            for (var i = parameterIndex - 1; i >= 0; i--)
            {
                var statement = TryFindFieldOrPropertyAssignmentStatement(
                    methodSymbol.Parameters[i], blockStatementOpt, out var fieldOrProperty);

                if (statement != null &&
                    fieldOrProperty is TSymbol symbol)
                {
                    var symbolSyntax = symbol.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
                    if (symbolSyntax.Ancestors().Contains(typeDeclaration))
                    {
                        // Found an existing field/property that corresponds to a preceding parameter.
                        // Place ourselves directly after it.
                        return new CodeGenerationOptions(afterThisLocation: symbolSyntax.GetLocation());
                    }
                }
            }

            for (var i = parameterIndex + 1; i < methodSymbol.Parameters.Length; i++)
            {
                var statement = TryFindFieldOrPropertyAssignmentStatement(
                    methodSymbol.Parameters[i], blockStatementOpt, out var fieldOrProperty);

                if (statement != null &&
                    fieldOrProperty is TSymbol symbol)
                {
                    // Found an existing field/property that corresponds to a following parameter.
                    // Place ourselves directly before it.
                    var symbolSyntax = symbol.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
                    if (symbolSyntax.Ancestors().Contains(typeDeclaration))
                    {
                        return new CodeGenerationOptions(beforeThisLocation: symbolSyntax.GetLocation());
                    }
                }
            }

            return null;
        }

        private SyntaxNode TryGetStatementToAddInitializationAfter(
            SemanticModel semanticModel,
            IParameterSymbol parameter,
            IBlockStatement blockStatementOpt,
            CancellationToken cancellationToken)
        {
            var methodSymbol = (IMethodSymbol)parameter.ContainingSymbol;
            var parameterIndex = methodSymbol.Parameters.IndexOf(parameter);

            // look for an existing assignment for a parameter that comes before us.
            // If we find one, we'll add ourselves after that parameter check.
            for (var i = parameterIndex - 1; i >= 0; i--)
            {
                var statement = TryFindFieldOrPropertyAssignmentStatement(
                    methodSymbol.Parameters[i], blockStatementOpt);
                if (statement != null)
                {
                    return statement.Syntax;
                }
            }

            // look for an existing check for a parameter that comes before us.
            // If we find one, we'll add ourselves after that parameter check.
            for (var i = parameterIndex + 1; i < methodSymbol.Parameters.Length; i++)
            {
                var statement = TryFindFieldOrPropertyAssignmentStatement(
                    methodSymbol.Parameters[i], blockStatementOpt);
                if (statement != null)
                {
                    var statementIndex = blockStatementOpt.Statements.IndexOf(statement);
                    return statementIndex > 0 ? blockStatementOpt.Statements[statementIndex - 1].Syntax : null;
                }
            }

            // We couldn't find a reasonable location for the new initialization statement.
            // Just place ourselves after the last statement in the constructor.
            return TryGetLastStatement(blockStatementOpt);
        }

        private IOperation TryFindFieldOrPropertyAssignmentStatement(IParameterSymbol parameter, IBlockStatement blockStatementOpt)
            => TryFindFieldOrPropertyAssignmentStatement(parameter, blockStatementOpt, out var fieldOrProperty);

        private IOperation TryFindFieldOrPropertyAssignmentStatement(
            IParameterSymbol parameter, IBlockStatement blockStatementOpt, out ISymbol fieldOrProperty)
        {
            if (blockStatementOpt != null)
            {
                var containingType = parameter.ContainingType;
                foreach (var statement in blockStatementOpt.Statements)
                {
                    // look for something of the form:  "this.s = s" or "this.s = s ?? ..."
                    if (IsFieldOrPropertyAssignment(statement, containingType, out var assignmentExpression, out fieldOrProperty) &&
                        IsParameterReferenceOrCoalesceOfParameterReference(assignmentExpression, parameter))
                    {
                        return statement;
                    }
                }
            }

            fieldOrProperty = null;
            return null;
        }

        private static bool IsParameterReferenceOrCoalesceOfParameterReference(
           IAssignmentExpression assignmentExpression, IParameterSymbol parameter)
        {
            if (IsParameterReference(assignmentExpression.Value, parameter))
            {
                // We already have a member initialized with this parameter like:
                //      this.field = parameter
                return true;
            }

            if (UnwrapImplicitConversion(assignmentExpression.Value) is ICoalesceExpression coalesceExpression &&
                IsParameterReference(coalesceExpression.Expression, parameter))
            {
                // We already have a member initialized with this parameter like:
                //      this.field = parameter ?? ...
                return true;
            }

            return false;
        }

        private async Task<ISymbol> TryFindMatchingUninitializedFieldOrPropertySymbolAsync(
            Document document, IParameterSymbol parameter, IBlockStatement blockStatementOpt, CancellationToken cancellationToken)
        {
            // Look for a field/property that really looks like it corresponds to this parameter.
            // Use a variety of heuristics around the name/type to see if this is a match.

            var rules = await GetNamingRulesAsync(document, cancellationToken).ConfigureAwait(false);
            var parameterWords = GetParameterWordParts(parameter);

            var containingType = parameter.ContainingType;
            var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            // Walk through the naming rules against this parameter's name to see what
            // name the user would like for it as a member in this type.  Note that we
            // have some fallback rules that use the standard conventions around 
            // properties /fields so that can still find things even if the user has no
            // naming preferences set.

            foreach (var rule in rules)
            {
                var memberName = rule.NamingStyle.CreateName(parameterWords);
                foreach (var memberWithName in containingType.GetMembers(memberName))
                {
                    // We found members in our type with that name.  If it's a writable
                    // field that we could assign this parameter to, and it's not already
                    // been assigned to, then this field is a good candidate for us to
                    // hook up to.
                    if (memberWithName is IFieldSymbol field &&
                        !field.IsConst &&
                        IsImplicitConversion(compilation, source: parameter.Type, destination: field.Type) &&
                        !ContainsMemberAssignment(blockStatementOpt, field))
                    {
                        return field;
                    }


                    // If it's a writable property that we could assign this parameter to, and it's
                    // not already been assigned to, then this property is a good candidate for us to
                    // hook up to.
                    if (memberWithName is IPropertySymbol property &&
                        property.IsWritableInConstructor() &&
                        IsImplicitConversion(compilation, source: parameter.Type, destination: property.Type) &&
                        !ContainsMemberAssignment(blockStatementOpt, property))
                    {
                        return property;
                    }
                }
            }

            // Couldn't find any existing member.  Just return nothing so we can offer to
            // create a member for them.
            return null;
        }

        private bool ContainsMemberAssignment(
            IBlockStatement blockStatementOpt, ISymbol member)
        {
            if (blockStatementOpt != null)
            {
                foreach (var statement in blockStatementOpt.Statements)
                {
                    if (IsFieldOrPropertyAssignment(statement, member.ContainingType, out var assignmentExpression) &&
                        UnwrapImplicitConversion(assignmentExpression.Target) is IMemberReferenceExpression memberReference &&
                        member.Equals(memberReference.Member))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private async Task<ImmutableArray<NamingRule>> GetNamingRulesAsync(
            Document document, CancellationToken cancellationToken)
        {
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var namingStyleOptions = options.GetOption(SimplificationOptions.NamingPreferences);

            // Add our built-in-rules at the end so that we always respect user naming rules 
            // first, but we always have something to fall-back upon if there are no matches.
            var rules = namingStyleOptions.CreateRules().NamingRules.AddRange(s_builtInRules);
            return rules;
        }

        /// <summary>
        /// Get the individual words in the parameter name.  This way we can generate 
        /// appropriate field/property names based on the user's preference.
        /// </summary>
        private ImmutableArray<string> GetParameterWordParts(IParameterSymbol parameter)
        {
            var parts = StringBreaker.GetWordParts(parameter.Name);
            var result  = CreateWords(parts, parameter.Name);
            parts.Free();
            return result;
        }

        private ImmutableArray<string> CreateWords(ArrayBuilder<TextSpan> parts, string name)
        {
            var result = ArrayBuilder<string>.GetInstance(parts.Count);
            foreach (var part in parts)
            {
                result.Add(name.Substring(part.Start, part.Length));
            }

            return result.ToImmutableAndFree();
        }
    }
}
