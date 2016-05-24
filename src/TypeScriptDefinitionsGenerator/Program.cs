﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using CommandLine;
using Microsoft.AspNet.SignalR.Hubs;
using TypeLite;
using TypeScriptDefinitionsGenerator.Extensions;
using TypeScriptDefinitionsGenerator.SignalR;

namespace TypeScriptDefinitionsGenerator
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("==============" + AppDomain.CurrentDomain.BaseDirectory);
            Console.WriteLine("TypeScriptGenerator Stated: " + DateTime.Now.ToString("HH:mm:ss"));

            var options = new Options();
            if (Parser.Default.ParseArguments(args, options))
            {
                if (options.AttachDebugger)
                {
                    Debugger.Launch();
                    Debugger.Break(); 
                }
                Directory.CreateDirectory(options.OutputFilePath);
                LoadReferencedAssemblies(options.Assembly);
                GenerateTypeScriptContracts(options);
                GenerateSignalrHubs(options);
                if (options.GenerateWebApiActions)
                {
                    GenerateWebApiActions(options);
                }
            }
            Console.WriteLine("TypeScriptGenerator Finished: " + DateTime.Now.ToString("HH:mm:ss"));
        }

        private static void LoadReferencedAssemblies(string assembly)
        {
            var sourceAssemblyDirectory = Path.GetDirectoryName(assembly);

            foreach (var file in Directory.GetFiles(sourceAssemblyDirectory, "*.dll"))
            {
                try
                {
                    File.Copy(file, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, new FileInfo(file).Name), true);
                }
                catch (IOException ex)
                {
                    if (!ex.Message.Contains("because it is being used by another process")) throw;
                }
            }
        }

        private static void GenerateTypeScriptContracts(Options options)
        {
            //Add breakpoints, then uncomment these lines to enable debugging
            //Debugger.Launch();
            //Debugger.Break(); 
            var assembly = Assembly.LoadFrom(options.Assembly);
            Console.WriteLine("Loaded assembly");

            var generator = new TypeScriptFluent()
                .WithConvertor<Guid>(c => "string");

            //var types = assembly.GetTypes();
            //ProcessTypes(types, generator);

            // Get the WebAPI controllers...
            var controllers = assembly.GetTypes().Where(t => typeof(ApiController).IsAssignableFrom(t));

            // Get the return types...
            var actions = controllers
                .SelectMany(c => c.GetMethods()
                    .Where(m => m.IsPublic)
                    .Where(m => m.DeclaringType == c));
            ProcessMethods(actions, generator);

            var signalrHubs = assembly.GetTypes().Where(t => typeof (IHub).IsAssignableFrom(t));
            var methods = signalrHubs
                .SelectMany(h => h.GetMethods()
                    .Where(m => m.IsPublic)
                    .Where(m => m.GetBaseDefinition().DeclaringType == h));
            ProcessMethods(methods, generator);

            var clientInterfaceTypes = signalrHubs.Where(t => t.BaseType.IsGenericType)
                .Select(t => t.BaseType.GetGenericArguments()[0]);
            var clientMethods = clientInterfaceTypes
                .SelectMany(h => h.GetMethods()
                    .Where(m => m.IsPublic)
                    .Where(m => m.DeclaringType == h));
            ProcessMethods(clientMethods, generator);

            // Add all classes that are declared inside the specified namespace
            if (options.Namespaces != null && options.Namespaces.Any())
            {
                var types = assembly.GetTypes()
                    .Where(t => options.Namespaces.Any(n => (t.Namespace ?? "") == n));
                ProcessTypes(types, generator);
            }
            
            generator.AsConstEnums(false);
            var tsEnumDefinitions = generator.Generate(TsGeneratorOutput.Enums);
            File.WriteAllText(Path.Combine(options.OutputFilePath, "enums.ts"), tsEnumDefinitions);

            //Generate interface definitions for all classes
            var tsClassDefinitions = generator.Generate(TsGeneratorOutput.Properties | TsGeneratorOutput.Fields);
            File.WriteAllText(Path.Combine(options.OutputFilePath, "classes.d.ts"), tsClassDefinitions);
        }

        private static void ProcessMethods(IEnumerable<MethodInfo> methods, TypeScriptFluent generator)
        {
            var returnTypes = methods.Select(m => m.ReturnType);
            ProcessTypes(returnTypes, generator);
            var inputTypes = methods.SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);
            ProcessTypes(inputTypes, generator);
        }

        private static void GenerateSignalrHubs(Options options)
        {
            var assembly = Assembly.LoadFrom(options.Assembly);

            var output = new SignalRGenerator().GenerateHubs(assembly);

            // Don't create the output if we don't have any hubs!
            if (string.IsNullOrEmpty(output)) return;

            File.WriteAllText(Path.Combine(options.OutputFilePath, "hubs.d.ts"), output);
        }

        private static void GenerateWebApiActions(Options options)
        {
            var assembly = Assembly.LoadFrom(options.Assembly);
            var output = new StringBuilder("module Api {");
            //TODO: allow this is be configured
            output.Append(_interfaces);
            var controllers = assembly.GetTypes().Where(t => typeof(ApiController).IsAssignableFrom(t)).OrderBy(t => t.Name);

            foreach (var controller in controllers)
            {
                var controllerName = controller.Name.Replace("Controller", "");
                output.AppendFormat("\r\n  export class {0} {{\r\n", controllerName);
                var actions = controller.GetMethods()
                    .Where(m => m.IsPublic)
                    .Where(m => m.DeclaringType == controller)
                    .OrderBy(m => m.Name);

                // TODO: WebAPI supports multiple actions with the same name but different parameters - this doesn't!
                foreach (var action in actions)
                {
                    if (NotAnAction(action)) continue;

                    var httpMethod = GetHttpMethod(action);
                    var actionName = GetActionName(action);
                    var returnType = TypeConverter.GetTypeScriptName(action.ReturnType);

                    var actionParameters = GetActionParameters(action);
                    var routeParameters = GetRouteParameters(actionParameters);
                    var queryStringParameters = GetQueryStringParameters(actionParameters);
                    var dataParameter = actionParameters.FirstOrDefault(a => !a.FromUri && !a.RouteProperty);
                    var dataParameterName = dataParameter == null ? "null" : dataParameter.Name;

                    // allow ajax options to be passed in to override defaults
                    output.AppendFormat("    public static {0}({1}): JQueryPromise<{2}> {{\r\n", 
                        actionName, GetMethodParameters(actionParameters), returnType);
                    output.AppendFormat("      return ServiceCaller.{0}(\"api/{1}/{2}{3}{4}\", {5}, ajaxOptions);\r\n",
                        httpMethod, controllerName, actionName, routeParameters, queryStringParameters, dataParameterName);
                    output.AppendLine("    }");
                    output.AppendLine();
                }

                output.AppendLine("  }");
            }

            output.Append("}");

            File.WriteAllText(Path.Combine(options.OutputFilePath, "actions.ts"), output.ToString());
        }

        private static string GetQueryStringParameters(List<ActionParameterInfo> actionParameters)
        {
            var result = string.Join("&", actionParameters.Where(a => a.FromUri && !a.RouteProperty).Select(a => a.Name + "=\" + " + a.Name + " + \""));
            if (result != "") result = "?" + result;
            return result;
        }

        private static string GetRouteParameters(List<ActionParameterInfo> actionParameters)
        {
            var result = string.Join("/", actionParameters.Where(a => a.RouteProperty).Select(a => "\" + " + a.Name + " + \""));
            if (result != "") result = "/" + result;
            return result;
        }

        private static string GetMethodParameters(List<ActionParameterInfo> actionParameters)
        {
            var result = string.Join(", ", actionParameters.Select(a => a.Name + ": " + a.Type));
            if (result != "") result += ", ";
            result += "ajaxOptions: IExtendedAjaxSettings = null";
            return result;
        }

        private static List<ActionParameterInfo> GetActionParameters(MethodInfo action)
        {
            var result = new List<ActionParameterInfo>();
            var parameters = action.GetParameters();
            foreach (var parameterInfo in parameters)
            {
                var param = new ActionParameterInfo();
                param.Name = parameterInfo.Name;
                param.Type = TypeConverter.GetTypeScriptName(parameterInfo.ParameterType);

                var fromUri = parameterInfo.GetCustomAttributes<FromUriAttribute>().FirstOrDefault();
                if (fromUri != null)
                {
                    param.Name = fromUri.Name ?? param.Name;
                }
                var fromBody = parameterInfo.GetCustomAttributes<FromBodyAttribute>().FirstOrDefault();
                // Parameters are from the URL unless specified by a [FromBody] attribute.
                param.FromUri = fromBody == null;

                //TODO: Support route parameters that are not 'id', might be hard as will need to parse routing setup
                if (parameterInfo.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    param.RouteProperty = true;
                }
                param.Name = param.Name.ToCamelCase();
                result.Add(param);
            }

            return result;
        }

        private static string GetActionName(MethodInfo action)
        {
            // TODO: Support ActionNameAttribute
            return action.Name.ToCamelCase();
        }

        private static string GetHttpMethod(MethodInfo action)
        {
            // TODO: Support other http methods
            if (action.CustomAttributes.Any(a => a.AttributeType.Name == typeof(HttpPostAttribute).Name)) return "post";
            return "get";
        }

        private static bool NotAnAction(MethodInfo action)
        {
            return action.CustomAttributes.Any(a => a.AttributeType.Name == typeof (NonActionAttribute).Name);
        }

        private static void ProcessTypes(IEnumerable<Type> types, TypeScriptFluent generator)
        {
            foreach (var clrType in types.Where(t => t != typeof (void)))
            {
                var clrTypeToUse = clrType;
                if (typeof (Task).IsAssignableFrom(clrTypeToUse))
                {
                    if (clrTypeToUse.IsGenericType)
                    {
                        clrTypeToUse = clrTypeToUse.GetGenericArguments()[0];
                    }
                    else continue; // Ignore non-generic Task as we can't know what type it will really be
                }
                if (clrTypeToUse.IsNullable())
                {
                    clrTypeToUse = clrTypeToUse.GetUnderlyingNullableType();
                }
                // Ignore compiler generated types
                if (Attribute.GetCustomAttribute(clrTypeToUse, typeof (CompilerGeneratedAttribute)) != null)
                {
                    continue;
                }

                Console.WriteLine("Processing Type: " + clrTypeToUse);
                if (clrTypeToUse == typeof(string) || clrTypeToUse.IsPrimitive || clrTypeToUse == typeof(object)) continue;

                if (clrTypeToUse.IsArray)
                {
                    ProcessTypes(new[] { clrTypeToUse.GetElementType() }, generator);
                }
                else if (clrTypeToUse.IsGenericType)
                {
                    ProcessTypes(clrTypeToUse.GetGenericArguments(), generator);
                    bool isEnumerable = typeof (IEnumerable).IsAssignableFrom(clrTypeToUse);
                    if (!isEnumerable)
                    {
                        generator.ModelBuilder.Add(clrTypeToUse);
                    }
                }
                else
                {
                    generator.ModelBuilder.Add(clrTypeToUse);
                }
            }
        }

        private static string _interfaces = @"
  export interface IDictionary<T> {
     [key: string]: T;
  }

";

        private class ActionParameterInfo
        {
            public string Name { get; set; }
            public bool FromUri { get; set; }
            public bool RouteProperty { get; set; }
            public string Type { get; set; }
        }
    }
}