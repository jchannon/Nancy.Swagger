﻿using Nancy.Routing;
using Nancy.Swagger.Annotations.Attributes;
using Nancy.Swagger.Services;
using Swagger.ObjectModel;
using Swagger.ObjectModel.ApiDeclaration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nancy.Swagger.Annotations
{
    public class SwaggerAnnotationsConverter : SwaggerMetadataConverter
    {
        private NancyContext _context;
        private INancyModuleCatalog _moduleCatalog;

        public SwaggerAnnotationsConverter(INancyModuleCatalog moduleCatalog, NancyContext context)
        {
            _moduleCatalog = moduleCatalog;
            _context = context;
        }

        protected override IEnumerable<SwaggerModelData> RetrieveSwaggerModelData()
        {
            return GetDistinctModelTypes(RetrieveSwaggerRouteData())
                    .Select(type => CreateSwaggerModelData(type));
        }

        protected override IEnumerable<SwaggerRouteData> RetrieveSwaggerRouteData()
        {
            return _moduleCatalog
                .GetAllModules(_context)
                .SelectMany(ToSwaggerRouteData)
                .ToList();
        }

        private SwaggerModelData CreateSwaggerModelData(Type type)
        {
            // Only use properties which have a pulbic getter and setter
            var typeProperties = type.GetProperties()
                                        .Where(pi => pi.CanWrite && pi.GetSetMethod(true).IsPublic)
                                        .Where(pi => pi.CanRead && pi.GetGetMethod(true).IsPublic);

            var modelData = new SwaggerModelData(type)
            {
                Properties = typeProperties.Select(pi => CreateSwaggerModelPropertyData(pi)).ToList()
            };

            var modelAttr = type.GetAttribute<SwaggerModelAttribute>();
            if (modelAttr != null)
            {
                modelData.Description = modelAttr.Description;
            }

            return modelData;
        }

        private SwaggerModelPropertyData CreateSwaggerModelPropertyData(PropertyInfo pi)
        {
            var modelProperty = new SwaggerModelPropertyData
            {
                Type = pi.PropertyType,
                Name = pi.Name
            };

            foreach (var attr in pi.GetAttributes<SwaggerModelPropertyAttribute>())
            {
                modelProperty.Name = attr.Name ?? modelProperty.Name;
                modelProperty.Description = attr.Description ?? modelProperty.Description;
                modelProperty.Minimum = attr.GetNullableMinimum() ?? modelProperty.Minimum;
                modelProperty.Maximum = attr.GetNullableMaximum() ?? modelProperty.Maximum;
                modelProperty.Required = attr.GetNullableRequired() ?? modelProperty.Required;
                modelProperty.UniqueItems = attr.GetNullableUniqueItems() ?? modelProperty.UniqueItems;
                modelProperty.Enum = attr.Enum ?? modelProperty.Enum;
            }

            return modelProperty;
        }

        private SwaggerParameterData CreateSwaggerParameterData(ParameterInfo pi)
        {
            var parameter = new SwaggerParameterData
            {
                Name = pi.Name,
                ParameterModel = pi.ParameterType
            };

            var paramAttrs = pi.GetCustomAttributes(typeof(SwaggerRouteParamAttribute), true).Cast<SwaggerRouteParamAttribute>();
            if (!paramAttrs.Any())
            {
                parameter.Description = "Warning: no annotation found for this parameter";
                parameter.ParamType = ParameterType.Query; // Required, so use query as fallback

                return parameter;
            }

            foreach (var attr in paramAttrs)
            {
                parameter.Name = attr.Name ?? parameter.Name;
                parameter.ParamType = attr.GetNullableParamType() ?? parameter.ParamType;
                parameter.Required = attr.GetNullableRequired() ?? parameter.Required;
                parameter.Description = attr.Description ?? parameter.Description;
            }

            return parameter;
        }

        private SwaggerRouteData CreateSwaggerRouteData(INancyModule module, Route route, Dictionary<RouteId, MethodInfo> routeHandlers)
        {
            var data = new SwaggerRouteData
            {
                ApiPath = route.Description.Path,
                ResourcePath = module.ModulePath.EnsureForwardSlash(),

                OperationMethod = route.Description.Method.ToHttpMethod(),
                OperationNickname = route.Description.Path,
            };

            var routeId = RouteId.Create(module, route);
            var handler = routeHandlers.ContainsKey(routeId) ? routeHandlers[routeId] : null;
            if (handler == null)
            {
                data.OperationNotes = "[example]"; // TODO: Insert example how to annotate a route
                data.OperationSummary = "Warning: no annotated method found for this route";

                return data;
            }

            foreach (var attr in handler.GetAttributes<SwaggerRouteAttribute>())
            {
                data.OperationSummary = attr.Summary ?? data.OperationSummary;
                data.OperationNotes = attr.Notes ?? data.OperationNotes;
                data.OperationModel = attr.Response ?? data.OperationModel;
                data.OperationConsumes = attr.Consumes ?? data.OperationConsumes;
                data.OperationProduces = attr.Produces ?? data.OperationProduces;
            }

            data.OperationResponseMessages = handler.GetAttributes<SwaggerResponseAttribute>()
                .Select(attr => {
                    var msg = new ResponseMessage
                    {
                        Code = (int)attr.Code,
                        Message = attr.Message                    
                    };
                    
                    if (attr.Model != null) 
                    {
                        msg.ResponseModel = Primitive.IsPrimitive(attr.Model) ? Primitive.FromType(attr.Model).Type : attr.Model.DefaultModelId();
                    }

                    return msg;
                })
                .ToList();


            data.OperationParameters = handler.GetParameters()
                .Select(CreateSwaggerParameterData)
                .ToList();

            return data;
        }

        private IEnumerable<SwaggerRouteData> ToSwaggerRouteData(INancyModule module)
        {
            Func<IEnumerable<SwaggerRouteAttribute>, RouteId> getRouteId = (attrs) =>
            {
                return attrs.Select(attr => RouteId.Create(module, attr))
                            .FirstOrDefault(routeId => routeId.IsValid);
            };

            // Discover route handlers and put them in a Dictionary<RouteId, MethodInfo>
            var routeHandlers =
                module.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Select(methodInfo => new
                    {
                        RouteId = getRouteId(methodInfo.GetAttributes<SwaggerRouteAttribute>()),
                        MethodInfo = methodInfo
                    })
                    .Where(x => x.RouteId.IsValid)
                    .ToDictionary(
                        x => x.RouteId,
                        x => x.MethodInfo
                    );

            return module.Routes
                .Select(route => CreateSwaggerRouteData(module, route, routeHandlers));
        }
    }
}