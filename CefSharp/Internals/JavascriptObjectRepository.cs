// Copyright © 2014 The CefSharp Authors. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CefSharp.Event;

namespace CefSharp.Internals
{
    /// <summary>
    /// This class manages the registration of objects in the browser
    /// process to be exposed to JavaScript in the renderer process.
    /// Registration performs method, parameter, property type analysis
    /// of the registered objects into meta-data tied to reflection data 
    /// for later use.
    /// 
    /// This class also is the adaptation layer between the BrowserProcessService
    /// and the registered objects. This means when the renderer wants to call an 
    /// exposed method, get a property of an object, or
    /// set a property of an object in the browser process, that this
    /// class does deals with the previously created meta-data and invokes the correct
    /// behavior via reflection APIs.
    /// 
    /// All of the registered objects are tracked via meta-data for the objects 
    /// expressed starting with the JavaScriptObject type.
    /// </summary>
    public class JavascriptObjectRepository : IJavascriptObjectRepository
    {
        public const string AllObjects = "All";

        private static long lastId;

        public event EventHandler<JavascriptBindingEventArgs> ResolveObject;
        public event EventHandler<JavascriptBindingCompleteEventArgs> ObjectBoundInJavascript;
        public event EventHandler<JavascriptBindingMultipleCompleteEventArgs> ObjectsBoundInJavascript;

        /// <summary>
        /// A hash from assigned object ids to the objects,
        /// this is done to speed up finding the object in O(1) time
        /// instead of traversing the JavaScriptRootObject tree.
        /// </summary>
        private readonly Dictionary<long, JavascriptObject> objects = new Dictionary<long, JavascriptObject>();

        /// <summary>
        /// Has the browser this repository is associated with been initilized (set in OnAfterCreated)
        /// </summary>
        public bool IsBrowserInitialized { get; set; }

        public void Dispose()
        {
            ResolveObject = null;
            ObjectBoundInJavascript = null;
            ObjectsBoundInJavascript = null;
        }

        public bool HasBoundObjects
        {
            get { return objects.Count > 0; }
        }

        public bool IsBound(string name)
        {
            return objects.Values.Any(x => x.Name == name);
        }

        //Ideally this would internal, unfurtunately it's used in C++
        //and it's hard to expose internals
        public List<JavascriptObject> GetObjects(List<string> names = null)
        {
            //If there are no objects names or the count is 0 then we will raise
            //the resolve event then return all objects that are registered,
            //we'll only perform checking if object(s) of specific name is requested.
            var getAllObjects = names == null || names.Count == 0;
            if (getAllObjects)
            {
                RaiseResolveObjectEvent(AllObjects);

                return objects.Values.ToList();
            }

            foreach (var name in names)
            {
                if (!IsBound(name))
                {
                    RaiseResolveObjectEvent(name);
                }
            }

            var objectsByName = objects.Values.Where(x => names.Contains(x.JavascriptName)).ToList();

            //TODO: JSB Add another event that signals when no object matching a name
            //in the list was provided.
            return objectsByName;
        }

        public void ObjectsBound(List<Tuple<string, bool, bool>> objs)
        {
            var boundObjectHandler = ObjectBoundInJavascript;
            var boundObjectsHandler = ObjectsBoundInJavascript;
            if (boundObjectHandler != null || boundObjectsHandler != null)
            {
                //Execute on Threadpool so we don't unnessicarily block the CEF IO thread
                Task.Run(() =>
                {
                    foreach (var obj in objs)
                    {
                        boundObjectHandler?.Invoke(this, new JavascriptBindingCompleteEventArgs(this, obj.Item1, obj.Item2, obj.Item3));
                    }

                    boundObjectsHandler?.Invoke(this, new JavascriptBindingMultipleCompleteEventArgs(this, objs.Select(x => x.Item1).ToList()));
                });
            }
        }

        private JavascriptObject CreateJavascriptObject(bool camelCaseJavascriptNames)
        {
            var id = Interlocked.Increment(ref lastId);

            var result = new JavascriptObject { Id = id, CamelCaseJavascriptNames = camelCaseJavascriptNames };
            objects[id] = result;

            return result;
        }

        public void Register(string name, object value, bool isAsync, BindingOptions options)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            //Enable WCF if not already enabled - can only be done before the browser has been initliazed
            //if done after the subprocess won't be WCF enabled it we'll have to throw an exception
            if (!IsBrowserInitialized && !isAsync)
            {
                CefSharpSettings.WcfEnabled = true;
            }

            if (!CefSharpSettings.WcfEnabled && !isAsync)
            {
                throw new InvalidOperationException(@"To enable synchronous JS bindings set WcfEnabled true in CefSharpSettings before you create
                                                    your ChromiumWebBrowser instances.");
            }

            //Validation name is unique
            if (objects.Values.Count(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                throw new ArgumentException("Object already bound with name:" + name, name);
            }

            //Binding of System types is problematic, so we don't support it
            var type = value.GetType();
            if (type.IsPrimitive || type.BaseType.Namespace.StartsWith("System."))
            {
                throw new ArgumentException("Registering of .Net framework built in types is not supported, " +
                    "create your own Object and proxy the calls if you need to access a Window/Form/Control.", "value");
            }

            var camelCaseJavascriptNames = options == null ? true : options.CamelCaseJavascriptNames;
            var jsObject = CreateJavascriptObject(camelCaseJavascriptNames);
            jsObject.Value = value;
            jsObject.Name = name;
            jsObject.JavascriptName = name;
            jsObject.IsAsync = isAsync;
            jsObject.Binder = options?.Binder;
            jsObject.MethodInterceptor = options?.MethodInterceptor;

            AnalyseObjectForBinding(jsObject, analyseMethods: true, analyseProperties: !isAsync, readPropertyValue: false, camelCaseJavascriptNames: camelCaseJavascriptNames);
        }

        public void UnRegisterAll()
        {
            objects.Clear();
        }

        public bool UnRegister(string name)
        {
            foreach (var kvp in objects)
            {
                if (string.Equals(kvp.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    objects.Remove(kvp.Key);

                    return true;
                }
            }

            return false;
        }

        internal struct MethodCallResult {
            public bool success;
            public object result;
            public string exception;
        }

        internal async Task<MethodCallResult> TryCallMethod(long objectId, string name, object[] parameters)
        {
            var callResult = new MethodCallResult() {
                exception = "",
                result = null,
                success = false
            };

            JavascriptObject obj;
            if (!objects.TryGetValue(objectId, out obj))
            {
                return callResult;
            }

            var method = obj.Methods.FirstOrDefault(p => p.JavascriptName == name);
            if (method == null)
            {
                throw new InvalidOperationException(string.Format("Method {0} not found on Object of Type {1}", name, obj.Value.GetType()));
            }

            try
            {
                //Check if the bound object method contains a ParamArray as the last parameter on the method signature.
                //NOTE: No additional parameters are permitted after the params keyword in a method declaration,
                //and only one params keyword is permitted in a method declaration.
                //https://msdn.microsoft.com/en-AU/library/w5zay9db.aspx
                if (method.HasParamArray)
                {
                    var paramList = new List<object>(method.Parameters.Count);

                    //Loop through all of the method parameters on the bound object.
                    for (var i = 0; i < method.Parameters.Count; i++)
                    {
                        //If the method parameter is a paramArray IE: (params string[] args)
                        //grab the parameters from the javascript function starting at the current bound object parameter index
                        //and add create an array that will be passed in as the last bound object method parameter.
                        if (method.Parameters[i].IsParamArray)
                        {
                            var convertedParams = new List<object>();
                            for (var s = i; s < parameters.Length; s++)
                            {
                                convertedParams.Add(parameters[s]);
                            }
                            paramList.Add(convertedParams.ToArray());
                        }
                        else
                        {
                            var jsParam = parameters.ElementAtOrDefault(i);
                            paramList.Add(jsParam);
                        }
                    }

                    parameters = paramList.ToArray();
                }

                //Check for parameter count missmatch between the parameters on the javascript function and the
                //number of parameters on the bound object method. (This is relevant for methods that have default values)
                //NOTE it's possible to have default params and a paramArray, so check missing params last
                var missingParams = method.ParameterCount - parameters.Length;

                if (missingParams > 0)
                {
                    var paramList = new List<object>(parameters);

                    for (var i = 0; i < missingParams; i++)
                    {
                        paramList.Add(Type.Missing);
                    }

                    parameters = paramList.ToArray();
                }

                try
                {
                    if (obj.Binder != null)
                    {
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            if (parameters[i] != null)
                            {
                                var paramExpectedType = method.Parameters[i].Type;
                                var paramType = parameters[i].GetType();
                                if (typeof(IDictionary<string, object>).IsAssignableFrom(paramType))
                                {
                                    var dictionary = (IDictionary<string, object>)parameters[i];
                                    parameters[i] = obj.Binder.Bind(dictionary, paramExpectedType);
                                }
                                else if (typeof(IList<object>).IsAssignableFrom(paramType))
                                {
                                    var list = (IList<object>)parameters[i];
                                    parameters[i] = obj.Binder.Bind(list, paramExpectedType);
                                }
                            }
                        }
                    }

                    if (obj.MethodInterceptor == null)
                    {
                        callResult.result = await method.Function(obj.Value, parameters);
                    }
                    else
                    {
                        callResult.result = obj.MethodInterceptor.Intercept(() => method.Function(obj.Value, parameters), method.ManagedName);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("Could not execute method: " + name + "(" + String.Join(", ", parameters) + ") " + (missingParams > 0 ? "- Missing Parameters: " + missingParams : ""), e);
                }

                //For sync binding with methods that return a complex property we create a new JavascriptObject
                //TODO: Fix the memory leak, every call to a method that returns an object will create a new
                //JavascriptObject and they are never released
                if (!obj.IsAsync && callResult.result != null && IsComplexType(callResult.result.GetType()))
                {
                    var jsObject = CreateJavascriptObject(obj.CamelCaseJavascriptNames);
                    jsObject.Value = callResult.result;
                    jsObject.Name = "FunctionResult(" + name + ")";
                    jsObject.JavascriptName = jsObject.Name;

                    AnalyseObjectForBinding(jsObject, analyseMethods: false, analyseProperties: true, readPropertyValue: true, camelCaseJavascriptNames: obj.CamelCaseJavascriptNames);

                    callResult.result = jsObject;
                }

                callResult.success = true;
                return callResult;
            }
            catch (TargetInvocationException e)
            {
                var baseException = e.GetBaseException();
                callResult.exception = baseException.ToString();
            }
            catch (Exception ex)
            {
                callResult.exception = ex.ToString();
            }

            callResult.success = false;
            return callResult;
        }

        internal bool TryGetProperty(long objectId, string name, out object result, out string exception)
        {
            exception = "";
            result = null;
            JavascriptObject obj;
            if (!objects.TryGetValue(objectId, out obj))
            {
                return false;
            }

            var property = obj.Properties.FirstOrDefault(p => p.JavascriptName == name);
            if (property == null)
            {
                throw new InvalidOperationException(string.Format("Property {0} not found on Object of Type {1}", name, obj.Value.GetType()));
            }

            try
            {
                result = property.GetValue(obj.Value);

                return true;
            }
            catch (Exception ex)
            {
                exception = ex.ToString();
            }

            return false;
        }

        internal bool TrySetProperty(long objectId, string name, object value, out string exception)
        {
            exception = "";
            JavascriptObject obj;
            if (!objects.TryGetValue(objectId, out obj))
            {
                return false;
            }

            var property = obj.Properties.FirstOrDefault(p => p.JavascriptName == name);
            if (property == null)
            {
                throw new InvalidOperationException(string.Format("Property {0} not found on Object of Type {1}", name, obj.Value.GetType()));
            }
            try
            {
                property.SetValue(obj.Value, value);

                return true;
            }
            catch (Exception ex)
            {
                exception = ex.ToString();
            }

            return false;
        }

        /// <summary>
        /// Analyse the object and generate metadata which will
        /// be used by the browser subprocess to interact with Cef.
        /// Method is called recursively
        /// </summary>
        /// <param name="obj">Javascript object</param>
        /// <param name="analyseMethods">Analyse methods for inclusion in metadata model</param>
        /// <param name="analyseProperties">Analyse properties for inclusion in metadata model</param>
        /// <param name="readPropertyValue">When analysis is done on a property, if true then get it's value for transmission over WCF</param>
        /// <param name="camelCaseJavascriptNames">camel case the javascript names of properties/methods</param>
        private void AnalyseObjectForBinding(JavascriptObject obj, bool analyseMethods, bool analyseProperties, bool readPropertyValue, bool camelCaseJavascriptNames)
        {
            if (obj.Value == null)
            {
                return;
            }

            var type = obj.Value.GetType();
            if (type.IsPrimitive || type == typeof(string))
            {
                return;
            }

            if (analyseMethods)
            {
                foreach (var methodInfo in type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(p => !p.IsSpecialName))
                {
                    // Type objects can not be serialized.
                    if (methodInfo.ReturnType == typeof(Type) || Attribute.IsDefined(methodInfo, typeof(JavascriptIgnoreAttribute)))
                    {
                        continue;
                    }

                    var jsMethod = CreateJavaScriptMethod(methodInfo, camelCaseJavascriptNames);
                    obj.Methods.Add(jsMethod);
                }

                // Allow calling properties of ExpandoObject that have a delegate value type e.g. Func<string, bool>
                if (type == typeof(System.Dynamic.ExpandoObject)) 
                {

                    var expando = obj.Value as System.Dynamic.ExpandoObject;
                    var memberDict = (IDictionary<string, object>)expando;

                    foreach (var pair in memberDict.Where(p => p.Value is Delegate &&
                                                               !obj.Methods.Any(m => m.ManagedName == p.Key)))
                    {
                        string propertyName = pair.Key;
                        Delegate methodDelegate = (Delegate)pair.Value;
                        var methodInfo = methodDelegate.Method;

                        // Need to use the property name in the parent object, not the name of the method the delegate points to.
                        var jsMethod = CreateJavaScriptMethod(methodInfo, camelCaseJavascriptNames, propertyName);

                        // Support for async methods.
                        bool methodReturnsTask = typeof(Task).IsAssignableFrom(methodInfo.ReturnType);
                        bool taskHasReturnValue = methodReturnsTask && methodInfo.ReturnType.IsGenericType;
                        PropertyInfo resultProperty = methodInfo.ReturnType?.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);

                        if (methodReturnsTask && taskHasReturnValue) {
                            // Async methods with return type Task actually have return type Task<VoidTaskResult> when invoked through reflection.
                            var voidTaskType = typeof(Task<>).MakeGenericType(Type.GetType("System.Threading.Tasks.VoidTaskResult"));
                            taskHasReturnValue = !voidTaskType.IsAssignableFrom(methodInfo.ReturnType);
                        }
                        
                        // Allow the delegate (value of the property) to have changed and call the current delegate.
                        jsMethod.Function = async (parentObject, args) => 
                        {
                            // TODO: The delegate return type may also have changed?
                            var currentMemberDict = (IDictionary<string, object>)parentObject;
                            var currentDelegate = (Delegate)currentMemberDict[jsMethod.ManagedName];

                            var callResult = currentDelegate.DynamicInvoke(args);
                            
                            if (callResult != null && methodReturnsTask) {

                                await (Task)callResult;

                                if (taskHasReturnValue) {
                                    // Getting the Result property from Task blocks until completion.
                                    object resultValue = resultProperty.GetValue(callResult);
                                    return resultValue;
                                } else {
                                    return null;
                                }

                            } else {

                                return callResult;
                            }
                        };

                        obj.Methods.Add(jsMethod);
                    }
                }

                // Allow calling any non-dynamic properties that have a delegate value type e.g. Func<string, bool>
                foreach (var propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => !p.IsSpecialName)) 
                {
                    
                    var hasIgnoreAttribute = Attribute.IsDefined(propertyInfo, typeof(JavascriptIgnoreAttribute));
                    if (!typeof(Delegate).IsAssignableFrom(propertyInfo.PropertyType) || hasIgnoreAttribute || !propertyInfo.CanRead) 
                    {
                        continue;
                    }

                    // This doesn't require the property to be set before registration.
                    // Compiler generates an Invoke method of correct form automatically.
                    var methodInfo = propertyInfo.PropertyType.GetMethod("Invoke");

                    // Need to use the property name in the parent object, not the name of the method the delegate points to.
                    var jsMethod = CreateJavaScriptMethod(methodInfo, camelCaseJavascriptNames, propertyInfo.Name);

                    // Allow the delegate (value of the property) to have changed and call the current delegate.
                    jsMethod.Function = async (parentObject, args) => 
                    {
                        var currentDelegate = (Delegate)propertyInfo.GetValue(parentObject);
                        return currentDelegate.DynamicInvoke(args);
                    };

                    obj.Methods.Add(jsMethod);
                }

            }

            if (analyseProperties)
            {
                foreach (var propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => !p.IsSpecialName))
                {
                    //https://msdn.microsoft.com/en-us/library/system.reflection.propertyinfo.getindexparameters(v=vs.110).aspx
                    //An array of type ParameterInfo containing the parameters for the indexes. If the property is not indexed, the array has 0 (zero) elements.
                    //According to MSDN array has zero elements when it's not an indexer, so in theory no null check is required
                    var isIndexer = propertyInfo.GetIndexParameters().Length > 0;
                    var hasIgnoreAttribute = Attribute.IsDefined(propertyInfo, typeof(JavascriptIgnoreAttribute));
                    if (propertyInfo.PropertyType == typeof(Type) || isIndexer || hasIgnoreAttribute)
                    {
                        continue;
                    }

                    var jsProperty = CreateJavaScriptProperty(propertyInfo, camelCaseJavascriptNames);
                    if (jsProperty.IsComplexType)
                    {
                        var jsObject = CreateJavascriptObject(camelCaseJavascriptNames);
                        jsObject.Name = propertyInfo.Name;
                        jsObject.JavascriptName = GetJavascriptName(propertyInfo.Name, camelCaseJavascriptNames);
                        jsObject.Value = jsProperty.GetValue(obj.Value);
                        jsProperty.JsObject = jsObject;

                        AnalyseObjectForBinding(jsProperty.JsObject, analyseMethods, analyseProperties: true, readPropertyValue: readPropertyValue, camelCaseJavascriptNames: camelCaseJavascriptNames);
                    }
                    else if (readPropertyValue)
                    {
                        jsProperty.PropertyValue = jsProperty.GetValue(obj.Value);
                    }
                    obj.Properties.Add(jsProperty);
                }
            }
        }

        private void RaiseResolveObjectEvent(string name)
        {
            ResolveObject?.Invoke(this, new JavascriptBindingEventArgs(this, name));
        }

        private static JavascriptMethod CreateJavaScriptMethod(MethodInfo methodInfo, bool camelCaseJavascriptNames, string managedName = null)
        {
            var jsMethod = new JavascriptMethod();

            jsMethod.ManagedName = managedName ?? methodInfo.Name;
            jsMethod.JavascriptName = GetJavascriptName(jsMethod.ManagedName, camelCaseJavascriptNames);
            jsMethod.Function = async (obj, param) => {
                return methodInfo.Invoke(obj, param);
            };

            jsMethod.ParameterCount = methodInfo.GetParameters().Length;
            jsMethod.Parameters = methodInfo.GetParameters()
                .Select(t => new MethodParameter()
                {
                    IsParamArray = t.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0,
                    Type = t.ParameterType
                }).ToList();
            //Pre compute HasParamArray for a very minor performance gain 
            jsMethod.HasParamArray = jsMethod.Parameters.LastOrDefault(t => t.IsParamArray) != null;

            return jsMethod;
        }

        private static JavascriptProperty CreateJavaScriptProperty(PropertyInfo propertyInfo, bool camelCaseJavascriptNames)
        {
            var jsProperty = new JavascriptProperty();

            jsProperty.ManagedName = propertyInfo.Name;
            jsProperty.JavascriptName = GetJavascriptName(propertyInfo.Name, camelCaseJavascriptNames);
            jsProperty.SetValue = (o, v) => propertyInfo.SetValue(o, v, null);
            jsProperty.GetValue = (o) => propertyInfo.GetValue(o, null);

            jsProperty.IsComplexType = IsComplexType(propertyInfo.PropertyType);
            jsProperty.IsReadOnly = !propertyInfo.CanWrite;

            return jsProperty;
        }

        private static bool IsComplexType(Type type)
        {
            if (type == typeof(void))
            {
                return false;
            }

            var baseType = type;

            var nullable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

            if (nullable)
            {
                baseType = Nullable.GetUnderlyingType(type);
            }

            if (baseType == null || baseType.IsArray || baseType.Namespace.StartsWith("System"))
            {
                return false;
            }

            if (baseType.IsValueType && !baseType.IsPrimitive && !baseType.IsEnum)
            {
                return false;
            }

            return !baseType.IsPrimitive && baseType != typeof(string);
        }

        private static string GetJavascriptName(string str, bool camelCaseJavascriptNames)
        {
            if (!camelCaseJavascriptNames)
            {
                return str;
            }

            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            return char.ToLowerInvariant(str[0]) + str.Substring(1);
        }
    }
}
