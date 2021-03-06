﻿using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.CSharp.TypeSystem;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpMinifier
{
	public class Minifier
	{
		private static string[] NameKeys = new string[] { "Name", "LiteralValue", "Keyword" };
		public static string ParserTempFileName = "temp.cs";

		private CSharpUnresolvedFile _unresolvedFile;
		private IProjectContent _projectContent;
		private ICompilation _compilation;
		private CSharpAstResolver _resolver;

		public SyntaxTree SyntaxTree
		{
			get;
			private set;
		}

		public MinifierOptions Options
		{
			get;
			private set;
		}

		public List<string> IgnoredIdentifiers
		{
			get;
			private set;
		}

		public List<string> IgnoredComments
		{
			get;
			private set;
		}

		public Minifier(MinifierOptions options = null, string[] ignoredIdentifiers = null, string[] ignoredComments = null)
		{
			Options = options ?? new MinifierOptions();

			if (Options.IdentifiersCompressing)
			{
				_projectContent = new CSharpProjectContent();
				var assemblies = new List<Assembly>
				{
					typeof(object).Assembly, // mscorlib
					typeof(Uri).Assembly, // System.dll
					typeof(Enumerable).Assembly, // System.Core.dll
				};

				var unresolvedAssemblies = new IUnresolvedAssembly[assemblies.Count];
				Parallel.For(
					0, assemblies.Count,
					delegate(int i)
					{
						var loader = new CecilLoader();
						var path = assemblies[i].Location;
						unresolvedAssemblies[i] = loader.LoadAssemblyFile(assemblies[i].Location);
					});
				_projectContent = _projectContent.AddAssemblyReferences((IEnumerable<IUnresolvedAssembly>)unresolvedAssemblies);
			}

			IgnoredIdentifiers = ignoredIdentifiers == null ? new List<string>() : ignoredIdentifiers.ToList();
			IgnoredComments = new List<string>();
			if (ignoredComments != null)
				foreach (var comment in ignoredComments)
				{
					var str = comment;
					if (str.StartsWith("//"))
						str = str.Substring("//".Length);
					else if (str.StartsWith("/*") && str.EndsWith("*/"))
						str = str.Substring("/*".Length, str.Length - "/*".Length - "*/".Length);
					if (!IgnoredComments.Contains(str))
						IgnoredComments.Add(str);
				}
		}

		public string MinifyFiles(string[] csFiles)
		{
			CSharpParser parser = new CSharpParser();
			SyntaxTree[] trees = csFiles.Select(file => parser.Parse(file, file + "_" + ParserTempFileName)).ToArray();

			SyntaxTree globalTree = new SyntaxTree();
			globalTree.FileName = ParserTempFileName;

			var usings = new List<UsingDeclaration>();
			var types = new List<TypeDeclaration>();
			foreach (var tree in trees)
			{
				List<UsingDeclaration> treeUsings = new List<UsingDeclaration>();
				GetUsingsAndRemoveUsingsAndNamespaces(tree, treeUsings, types);
				usings.AddRange(treeUsings.Where(u1 => !usings.Exists(u2 => u2.Namespace == u1.Namespace)));
			}

			foreach (var u in usings)
				globalTree.AddChild(u.Clone(), new Role<AstNode>("UsingDeclaration"));
			foreach (var t in types)
				globalTree.AddChild(t.Clone(), new Role<AstNode>("TypeDeclaration"));

			SyntaxTree = globalTree;

			return Minify();
		}

		public string MinifyFromString(string csharpCode)
		{
			SyntaxTree = new CSharpParser().Parse(csharpCode, ParserTempFileName);

			return Minify();
		}

		public string Minify()
		{
			if (Options.CommentsRemoving || Options.RegionsRemoving)
				RemoveCommentsAndRegions();

			if (Options.IdentifiersCompressing)
			{
				CompressIdentifiers();
			}

			if (Options.MiscCompressing)
				CompressMisc();

			string result;
			if (Options.SpacesRemoving)
				result = ToStringWithoutSpaces();
			else
				result = SyntaxTree.GetText();

			return result;
		}

		private void GetUsingsAndRemoveUsingsAndNamespaces(SyntaxTree tree, List<UsingDeclaration> usings, List<TypeDeclaration> types)
		{
			foreach (var child in tree.Children)
				GetUsingsAndRemoveUsingsAndNamespaces(child, usings, types);
		}

		private void GetUsingsAndRemoveUsingsAndNamespaces(AstNode node, List<UsingDeclaration> usings, List<TypeDeclaration> types)
		{
			if (node.Role.ToString() == "Member" && node.GetType().Name == "UsingDeclaration")
			{
				usings.Add((UsingDeclaration)node);
			}
			else if (node.Role.ToString() == "Member" && node.GetType().Name == "NamespaceDeclaration")
			{
				var parent = node.Parent;
				foreach (var child in node.Children)
				{
					if (child.NodeType == NodeType.TypeDeclaration)
					{
						types.Add((TypeDeclaration)child);
					}
					else if (child.ToString() == "Member" && child.GetType().Name == "NamespaceDeclaration")
					{
						GetUsingsAndRemoveUsingsAndNamespaces(child, usings, types);
					}
				}
			}
			else
			{
				if (node.Children.Count() >= 1)
				{
					foreach (var child in node.Children)
						GetUsingsAndRemoveUsingsAndNamespaces(child, usings, types);
				}
			}
		}

		#region Misc Compression

		private void CompressMisc()
		{
			foreach (var children in SyntaxTree.Children)
				CompressMisc(children);
		}

		private void CompressMisc(AstNode node)
		{
			foreach (var children in node.Children)
			{
				if (children is PrimitiveExpression)
				{
					var primitiveExpression = ((PrimitiveExpression)children);
					if (IsIntegerNumber(primitiveExpression.Value))
					{
						var str = primitiveExpression.Value.ToString();
						long number;
						if (long.TryParse(str, out number))
						{
							string hex = "0x" + number.ToString("X");
							primitiveExpression.LiteralValue = str.Length < hex.Length ? str : hex;
						}
					}
				}
				else if (children is CSharpModifierToken)
				{
					var modifier = ((CSharpModifierToken)children).Modifier;
					if ((modifier & Modifiers.Private) == Modifiers.Private && (modifier & ~Modifiers.Private) == 0)
						children.Remove();
					else
						modifier &= ~Modifiers.Private;
				}
				else
				{
					if (children is BlockStatement && children.Role.ToString() != "Body")
					{
						var childrenCount = children.Children.Count();
						if (childrenCount == 3)
							children.ReplaceWith(children.Children.ElementAt(1));
						else if (childrenCount < 3)
							children.Remove();
					}
					CompressMisc(children);
				}
			}
		}

		public static bool IsIntegerNumber(object value)
		{
			return value is sbyte || value is byte ||
				value is short || value is ushort || value is int ||
				value is uint || value is long || value is ulong;
		}

		#endregion

		#region Comments & Regions Removing

		private void RemoveCommentsAndRegions()
		{
			RemoveCommentsAndRegions(SyntaxTree);
		}

		private void RemoveCommentsAndRegions(AstNode node)
		{
			foreach (var children in node.Children)
			{
				if (Options.CommentsRemoving && children is Comment)
				{
					var properties = children.GetProperties();
					var commentType = properties.GetPropertyValueEnum<CommentType>(children, "CommentType");
					var content = properties.GetPropertyValue(children, "Content");
					if (!IgnoredComments.Contains(content) && commentType != CommentType.InactiveCode)
						children.Remove();
				}
				else if (Options.RegionsRemoving && children is PreProcessorDirective)
				{
					var type = children.GetPropertyValueEnum<PreProcessorDirectiveType>("Type");
					switch (type)
					{
						case PreProcessorDirectiveType.Region:
						case PreProcessorDirectiveType.Endregion:
						case PreProcessorDirectiveType.Warning:
							children.Remove();
							break;
					}
				}
				else
					RemoveCommentsAndRegions(children);
			}
		}

		#endregion

		#region Identifiers Compressing

		private void CompressIdentifiers()
		{
			CompressLocals();
			CompressMembers();
			CompressTypes();
		}

		private void CompressLocals()
		{
			Recompile();

			var defs = _compilation.GetAllTypeDefinitions();

			var localsVisitor = new MinifyLocalsAstVisitor(IgnoredIdentifiers);
			SyntaxTree.AcceptVisitor(localsVisitor);

			var idGenerator = new MinIdGenerator();
			var substitutor = new Substitutor(idGenerator);
			var ignoredNames = new List<string>(IgnoredIdentifiers);
			ignoredNames.AddRange(localsVisitor.NotMembersIdNames);
			var newSubstituton = substitutor.Generate(localsVisitor.MethodVars, ignoredNames.ToArray());

			foreach (var method in localsVisitor.MethodVars)
			{
				var m = newSubstituton[method.Key];
				foreach (NameNode v in method.Value)
					RenameLocals(v.Node, m[v.Name]);
			}
		}

		private void CompressMembers()
		{
			Recompile();

			var membersVisitor = new MinifyMembersAstVisitor(IgnoredIdentifiers, Options.ConsoleApp);
			SyntaxTree.AcceptVisitor(membersVisitor);

			var idGenerator = new MinIdGenerator();
			var substitutor = new Substitutor(idGenerator);
			var ignoredNames = new List<string>(IgnoredIdentifiers);
			ignoredNames.AddRange(membersVisitor.NotMembersIdNames);
			var newSubstituton = substitutor.Generate(membersVisitor.TypeMembers, ignoredNames.ToArray());

			foreach (var member in membersVisitor.TypeMembers)
			{
				var m = newSubstituton[member.Key];
				foreach (NameNode v in member.Value)
					RenameMembers(v.Node, m[v.Name]);
			}
		}

		private void CompressTypes()
		{
			Recompile();


		}

		private void RenameLocals(AstNode node, string newName)
		{
			LocalResolveResult resolveResult = _resolver.Resolve(node) as LocalResolveResult;
			if (resolveResult != null)
			{
				var findReferences = new FindReferences();
				FoundReferenceCallback callback = delegate(AstNode matchNode, ResolveResult result)
				{
					if (matchNode is ParameterDeclaration)
						((ParameterDeclaration)matchNode).Name = newName;
					else if (matchNode is VariableInitializer)
						((VariableInitializer)matchNode).Name = newName;
					else if (matchNode is IdentifierExpression)
						((IdentifierExpression)matchNode).Identifier = newName;
				};
				findReferences.FindLocalReferences(resolveResult.Variable, _unresolvedFile, SyntaxTree, _compilation, callback, CancellationToken.None);
			}
		}

		private void RenameMembers(AstNode node, string newName)
		{
			MemberResolveResult resolveResult = _resolver.Resolve(node) as MemberResolveResult;
			if (resolveResult != null)
			{
				var findReferences = new FindReferences();
				FoundReferenceCallback callback = delegate(AstNode matchNode, ResolveResult result)
				{
					if (matchNode is VariableInitializer)
						((VariableInitializer)matchNode).Name = newName;
					else if (matchNode is MethodDeclaration)
						((MethodDeclaration)matchNode).Name = newName;
					else if (matchNode is PropertyDeclaration)
						((PropertyDeclaration)matchNode).Name = newName;
					else if (matchNode is IndexerDeclaration)
						((IndexerDeclaration)matchNode).Name = newName;
					else if (matchNode is OperatorDeclaration)
						((OperatorDeclaration)matchNode).Name = newName;
					else if (matchNode is MemberReferenceExpression)
						((MemberReferenceExpression)matchNode).MemberName = newName;
					else if (matchNode is IdentifierExpression)
						((IdentifierExpression)matchNode).Identifier = newName;
					else if (matchNode is InvocationExpression)
						((IdentifierExpression)((InvocationExpression)matchNode).Target).Identifier = newName;
					else
					{
					}
				};
				var searchScopes = findReferences.GetSearchScopes(resolveResult.Member);
				findReferences.FindReferencesInFile(searchScopes, _unresolvedFile, SyntaxTree, _compilation, callback, CancellationToken.None);
			}
			else
			{
			}
		}

		private void Recompile()
		{
			_unresolvedFile = SyntaxTree.ToTypeSystem();
			_projectContent = _projectContent.AddOrUpdateFiles(_unresolvedFile);
			_compilation = _projectContent.CreateCompilation();
			_resolver = new CSharpAstResolver(_compilation, SyntaxTree, _unresolvedFile);
		}

		#endregion

		#region Removing of spaces and line breaks

		AstNode _prevNode;
		StringBuilder _line;
		StringBuilder _result;

		private string ToStringWithoutSpaces()
		{
			_result = new StringBuilder();
			_line = new StringBuilder(Options.LineLength);

			_prevNode = null;
			foreach (var children in SyntaxTree.Children)
			{
				RemoveSpacesAndAppend(children);
				if (children.Children.Count() <= 1)
					_prevNode = children;
			}
			_result.Append(_line);

			return _result.ToString();
		}

		private void RemoveSpacesAndAppend(AstNode node)
		{
			if (node.Children.Count() == 0)
			{
				string beginSymbols = " ";
				char last = (char)0;
				if (_line.Length != 0)
					last = _line[_line.Length - 1];

				if (last == ' ' || last == '\r' || last == '\n' || _prevNode == null || node == null)
					beginSymbols = "";
				else
				{
					var prevComment = _prevNode as Comment;
					if (prevComment != null)
					{
						if (prevComment.CommentType == CommentType.SingleLine || prevComment.CommentType == CommentType.Documentation)
							beginSymbols = Environment.NewLine;
						else
							beginSymbols = "";
					}
					else if (node is PreProcessorDirective || _prevNode is PreProcessorDirective)
						beginSymbols = Environment.NewLine;
					else
					{
						if ((_prevNode is CSharpTokenNode && _prevNode.Role.ToString().All(c => !char.IsLetterOrDigit(c))) ||
							(node is CSharpTokenNode && node.Role.ToString().All(c => !char.IsLetterOrDigit(c))) ||
							node is Comment)
								beginSymbols = "";
					}
				}

				string newString = beginSymbols + GetLeafNodeString(node);
				if (Options.LineLength == 0)
					_result.Append(newString);
				else
				{
					if (_line.Length + newString.Length > Options.LineLength)
					{
						_result.AppendLine(_line.ToString());
						_line.Clear();
						_line.Append(newString.TrimStart());
					}
					else
					{
						_line.Append(newString);
					}
				}
			}
			else
			{
				foreach (AstNode children in node.Children)
				{
					RemoveSpacesAndAppend(children);
					if (children.Children.Count() <= 1)
						_prevNode = children;
				}
			}
		}

		public static string GetLeafNodeString(AstNode node)
		{
			string nodeRole = node.Role.ToString();
			var properties = node.GetProperties();
			if (nodeRole == "Comment")
			{
				CommentType commentType = properties.GetPropertyValueEnum<CommentType>(node, "CommentType");
				string content = properties.GetPropertyValue(node, "Content");
				switch (commentType)
				{
					default:
					case CommentType.SingleLine:
						return "//" + content;
					case CommentType.Documentation:
						return "///" + content;
					case CommentType.MultiLine:
						return "/*" + content + "*/";
					case CommentType.InactiveCode:
						return content;
					case CommentType.MultiLineDocumentation:
						return "/**" + content + "*/";
				}
			}
			else if (nodeRole == "Modifier")
			{
				return properties.GetPropertyValue(node, "Modifier").ToLower();
			}
			else if (nodeRole == "Target" || nodeRole == "Right")
			{
				string typeName = node.GetType().Name;
				if (typeName == "ThisReferenceExpression")
					return "this";
				else if (typeName == "NullReferenceExpression")
					return "null";
			}
			else if (nodeRole == "PreProcessorDirective")
			{
				var type = properties.GetPropertyValue(node, "Type").ToLower();
				var argument = properties.GetPropertyValue(node, "Argument");
				var result = "#" + type;
				if (argument != string.Empty)
					result += " " + argument;
				return result;
			}

			if (node is ThisReferenceExpression)
				return "this";
			else if (node is NullReferenceExpression)
				return "null";
			else if (node is CSharpTokenNode || node is CSharpModifierToken)
				return nodeRole;

			return properties
				.Where(p => NameKeys.Contains(p.Name))
				.FirstOrDefault()
				.GetValue(node, null)
				.ToString();
		}

		#endregion
	}
}
