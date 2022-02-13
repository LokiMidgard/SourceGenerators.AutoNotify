﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SourceGenerators
{
    [Generator]
    public class AutoNotifyGenerator : ISourceGenerator
    {
        private const string MetadataName = "SourceGenerators.AutoNotifyAttribute";

        private const string attributeText = @"
using System;
namespace SourceGenerators
{
    #nullable enable
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    [System.Diagnostics.Conditional(""AutoNotifyGenerator_DEBUG"")]
    sealed class AutoNotifyAttribute : Attribute
    {
        public AutoNotifyAttribute()
        {
        }
        public string? PropertyName { get; set; }
    }
}
";


        public void Initialize(GeneratorInitializationContext context)
        {
            // Register the attribute source
            context.RegisterForPostInitialization((i) => i.AddSource("AutoNotifyAttribute", attributeText));

            // Register a syntax receiver that will be created for each generation pass
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // retrieve the populated receiver 
            if (!(context.SyntaxContextReceiver is SyntaxReceiver receiver))
                return;

            // get the added attribute, and INotifyPropertyChanged
            INamedTypeSymbol attributeSymbol = context.Compilation.GetTypeByMetadataName(MetadataName);
            INamedTypeSymbol notifySymbol = context.Compilation.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged");

            // group the fields by class, and generate the source
            foreach (IGrouping<INamedTypeSymbol, IFieldSymbol> group in receiver.Fields.GroupBy(f => f.ContainingType))
            {
                string classSource = ProcessClass(group.Key, group.ToList(), attributeSymbol, notifySymbol, context);
                if (classSource is not null)
                {

                    StringBuilder b = new StringBuilder();
                    ISymbol symbol = group.Key;
                    while (symbol is not null)
                    {
                        if (b.Length != 0)
                            b.Append('.');
                        b.Append(symbol.MetadataName);
                        symbol = symbol.ContainingSymbol;
                    }

                    context.AddSource($"{b}_autoNotify.cs", SourceText.From(classSource, Encoding.UTF8));
                }
            }
        }

        private string ProcessClass(INamedTypeSymbol classSymbol, List<IFieldSymbol> fields, ISymbol attributeSymbol, ISymbol notifySymbol, GeneratorExecutionContext context)
        {


            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
            StringBuilder source = new StringBuilder();
            // begin building the generated source
            int additionalClasses = 0;

            source.Append($@"
#nullable enable
namespace {namespaceName}
{{
");

            var queue = new Queue<ITypeSymbol>();
            var containing = classSymbol.ContainingSymbol;
            while (!containing.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
            {
                if (containing is ITypeSymbol type)
                {
                    queue.Enqueue(type);
                }
                else
                {
                    //TODO: issue a diagnostic that the containing type is not supported
                    return null;
                }
                containing = containing.ContainingSymbol;

            }

            additionalClasses = queue.Count;

            while (queue.Count > 0)
            {
                var type = queue.Dequeue();
                var kind = type.TypeKind switch
                {
                    TypeKind.Class => "class",
                    TypeKind.Struct => "struct",
                    _ => null
                };
                if (kind is null)
                    return null;

                source.Append($@"
    partial {kind} {type.Name}
    {{
");
            }



            source.Append($@"
    partial class {classSymbol.Name} : {notifySymbol.ToDisplayString()}
    {{
");


            // if the class doesn't implement INotifyPropertyChanged already, add it
            if (!classSymbol.Interfaces.Contains(notifySymbol))
            {
                source.Append("public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;");
            }

            // create properties for each field 
            foreach (IFieldSymbol fieldSymbol in fields)
            {
                ProcessField(source, fieldSymbol, attributeSymbol);
            }

            for (int i = 0; i < additionalClasses; i++)
            {
                source.Append("} ");
            }
            source.Append("} }");
            return source.ToString();
        }

        private void ProcessField(StringBuilder source, IFieldSymbol fieldSymbol, ISymbol attributeSymbol)
        {
            // get the name and type of the field
            string fieldName = fieldSymbol.Name;
            ITypeSymbol fieldType = fieldSymbol.Type;

            // get the AutoNotify attribute from the field, and any associated data
            AttributeData attributeData = fieldSymbol.GetAttributes().Single(ad => ad.AttributeClass.Equals(attributeSymbol, SymbolEqualityComparer.Default));
            TypedConstant overridenNameOpt = attributeData.NamedArguments.SingleOrDefault(kvp => kvp.Key == "PropertyName").Value;

            string propertyName = chooseName(fieldName, overridenNameOpt);
            if (propertyName.Length == 0 || propertyName == fieldName)
            {
                //TODO: issue a diagnostic that we can't process this field
                return;
            }

            source.Append($@"
public {fieldType} {propertyName} 
{{
    get 
    {{
        return this.{fieldName};
    }}

    set
    {{
        this.{fieldName} = value;
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof({propertyName})));
    }}
}}

");

            string chooseName(string fieldName, TypedConstant overridenNameOpt)
            {
                if (!overridenNameOpt.IsNull)
                {
                    return overridenNameOpt.Value.ToString();
                }

                fieldName = fieldName.TrimStart('_');
                if (fieldName.Length == 0)
                    return string.Empty;

                if (fieldName.Length == 1)
                    return fieldName.ToUpper();

                return fieldName.Substring(0, 1).ToUpper() + fieldName.Substring(1);
            }

        }

        /// <summary>
        /// Created on demand before each generation pass
        /// </summary>
        class SyntaxReceiver : ISyntaxContextReceiver
        {
            public List<IFieldSymbol> Fields { get; } = new List<IFieldSymbol>();

            /// <summary>
            /// Called for every syntax node in the compilation, we can inspect the nodes and save any information useful for generation
            /// </summary>
            public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
            {
                // any field with at least one attribute is a candidate for property generation
                if (context.Node is FieldDeclarationSyntax fieldDeclarationSyntax
                    && fieldDeclarationSyntax.AttributeLists.Count > 0)
                {
                    foreach (VariableDeclaratorSyntax variable in fieldDeclarationSyntax.Declaration.Variables)
                    {
                        // Get the symbol being declared by the field, and keep it if its annotated
                        IFieldSymbol fieldSymbol = context.SemanticModel.GetDeclaredSymbol(variable) as IFieldSymbol;
                        if (fieldSymbol.GetAttributes().Any(ad => ad.AttributeClass.ToDisplayString() == MetadataName))
                        {
                            Fields.Add(fieldSymbol);
                        }
                    }
                }
            }
        }
    }
}