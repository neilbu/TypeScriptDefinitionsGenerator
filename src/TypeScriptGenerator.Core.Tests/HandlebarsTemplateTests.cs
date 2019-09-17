using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HandlebarsDotNet;
using TypeScriptDefinitionsGenerator.Common;
using TypeScriptDefinitionsGenerator.Common.Extensions;
using TypeScriptDefinitionsGenerator.Common.Models;
using Xunit;
using Xunit.Abstractions;

namespace TypeScriptGenerator.Core.Tests
{
    public class HandlebarsTemplateTests
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public HandlebarsTemplateTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        [Fact]
        public void SimpleTemplateTest()
        {
            var template = "export module {{name}} {\r\n}";
            var result = Handlebars.Compile(template)(new {name = "MyModule"});

            Assert.Equal("export module MyModule {\r\n}", result);
        }

        [Fact]
        public void NestedTemplateTest()
        {
            var template = @"export module {{name}} {
{{#each classes}}
  {{> class}}

{{/each}}
}";
            var classTemplate = @"  public class {{className}} {}";
            var model = new
            {
                name = "MyModule",
                bob = "bob",
                classes = new[]
                {
                    new {className = "MyClass"}
                }
            };

            Handlebars.RegisterTemplate("class", classTemplate);

            var result = Handlebars.Compile(template)(model);

            Assert.Equal("export module MyModule {\r\n  public class MyClass {}\r\n}", result);
        }

        [Fact]
        public void BlockHelpersTest()
        {
            Handlebars.RegisterHelper("StringEqualityBlockHelper", (TextWriter output, HelperOptions options, dynamic context, object[] arguments) => {
                if (arguments.Length != 2)
                {
                    throw new HandlebarsException("{{StringEqualityBlockHelper}} helper must have exactly two argument");
                }
                string left = arguments[0] as string;
                string right = arguments[1] as string;
                if (left == right)
                {
                    options.Template(output, null);
                }
                else
                {
                    options.Inverse(output, null);
                }
            });
            Dictionary<string, string> animals = new Dictionary<string, string>() {
                {"Fluffy", "cat" },
                {"Fido", "dog" },
                {"Chewy", "hamster" }
            };
            var template = "{{#each @value}}The animal, {{@key}}, {{StringEqualityBlockHelper @value 'dog'}}is a dog{{else}}is not a dog{{/StringEqualityBlockHelper}}.\r\n{{/each}}";
            var compiledTemplate = Handlebars.Compile(template);
            var templateOutput = compiledTemplate(animals);
            
            Assert.Equal("The animal, Fluffy, is not a dog.\r\nThe animal, Fido, is a dog.\r\nThe animal, Chewy, is not a dog.\r\n", templateOutput);
        }

        [Fact]
        public void ServiceStackBlockHelperTest()
        {
            Handlebars.RegisterHelper("MaxRouteParametersForVerb", (TextWriter output, HelperOptions options, dynamic context, object[] arguments) =>
            {
                if (arguments.Length != 2) throw new HandlebarsException("{{MaxRouteParametersForVerb}} helper expects two parameters: input routes array, and http verb");
                var list = arguments[0] as IList<ServiceStackRouteInfo>;
                var verb = arguments[1] as string;
                if (!list.Any(x => x.Verb == verb)) options.Inverse(output, null);
                else options.Template(output, list.MaxBy(i => i.RouteParameters.Count));
            });

            var model = new ServiceStackRequestModel();
            model.Routes.Add(new ServiceStackRouteInfo("GET", "path", "url2", new List<string> {"test"}));
            model.Routes.Add(new ServiceStackRouteInfo("GET", "path", "url", new List<string>()));
            var template = "{{MaxRouteParametersForVerb Routes 'GET'}}{{Url}}{{/MaxRouteParametersForVerb}}";
            var compiledTemplate = Handlebars.Compile(template);
            var templateOutput = compiledTemplate(model);
            
            Assert.Equal("url2", templateOutput);
        }
        
                
        [Fact]
        public void ServiceStackBlockHelperTestUsingResourceFiles()
        {
            var gen = new MainGenerator(new Options(), new GenerationConfiguration());
            MainGenerator.SetupHelpers();
            gen.SetupTemplates("ServiceStack_Angular");

            var model = new ServiceStackApiModel();
            var requestModel = new ServiceStackRequestModel {Name = "MyRequest", ReturnTypeTypeScriptName = "string"};
            model.Requests.Add(requestModel);
            requestModel.Routes.Add(new ServiceStackRouteInfo("GET", "path", "url2", new List<string> {"test"}));
            requestModel.Routes.Add(new ServiceStackRouteInfo("GET", "path", "url", new List<string>()));
            
            var compiledTemplate = Handlebars.Compile("{{> main.hbs }}");
            var templateOutput = compiledTemplate(model);
            
            _testOutputHelper.WriteLine(templateOutput);
        }
    }
}