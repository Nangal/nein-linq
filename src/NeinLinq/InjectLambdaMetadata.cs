﻿using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace NeinLinq
{
    sealed class InjectLambdaMetadata
    {
        readonly bool config;

        public bool Config => config;

        readonly Lazy<Func<Expression, LambdaExpression>> lambda;

        public LambdaExpression Lambda(Expression value) => lambda.Value(value);

        InjectLambdaMetadata(bool config, Lazy<Func<Expression, LambdaExpression>> lambda)
        {
            this.config = config;
            this.lambda = lambda;
        }

        public static InjectLambdaMetadata Create(MethodInfo method)
        {
            var metadata = method.GetCustomAttribute<InjectLambdaAttribute>();

            var lambdaFactory = new Lazy<Func<Expression, LambdaExpression>>(() => LambdaFactory(method, metadata));

            return new InjectLambdaMetadata(metadata != null, lambdaFactory);
        }

        public static InjectLambdaMetadata Create(PropertyInfo property)
        {
            var metadata = property.GetCustomAttribute<InjectLambdaAttribute>()
                ?? property.GetMethod().GetCustomAttribute<InjectLambdaAttribute>();

            var lambdaFactory = new Lazy<Func<Expression, LambdaExpression>>(() => LambdaFactory(property, metadata));

            return new InjectLambdaMetadata(metadata != null, lambdaFactory);

        }

        static Func<Expression, LambdaExpression> LambdaFactory(MethodInfo method, InjectLambdaAttribute metadata)
        {
            // retrieve method's signature
            var args = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var result = method.ReturnParameter.ParameterType;

            // special ultra-fast treatment for static methods and sealed classes
            if (method.IsStatic || method.DeclaringType.GetTypeInfo().IsSealed)
            {
                return FixedLambdaFactory(metadata, method.DeclaringType, method.Name, args, result, !method.IsStatic);
            }

            // dynamic but not that fast treatment for other stuff
            return DynamicLambdaFactory(method.Name, args, result);
        }

        static Func<Expression, LambdaExpression> LambdaFactory(PropertyInfo property, InjectLambdaAttribute metadata)
        {
            // retrieve method's signature
            var args = new[] { property.DeclaringType };
            var result = property.PropertyType;

            // special treatment for super-heroic property getters
            return FixedLambdaFactory(metadata, property.DeclaringType, property.Name, args, result, false);
        }

        static Func<Expression, LambdaExpression> FixedLambdaFactory(InjectLambdaAttribute metadata, Type target, string method, Type[] args, Type result, bool instance)
        {
            // apply configuration, if any
            if (metadata != null)
            {
                if (metadata.Target != null)
                    target = metadata.Target;
                if (!string.IsNullOrEmpty(metadata.Method))
                    method = metadata.Method;
            }

            // retrieve validated factory method once
            var factory = FactoryMethod(target, method, args, result, instance);

            if (factory.IsStatic)
            {
                // compile factory call for performance reasons :-)
                return Expression.Lambda<Func<Expression, LambdaExpression>>(
                    Expression.Call(factory), Expression.Parameter(typeof(Expression))).Compile();
            }

            // call actual target object, compiles every time during execution... :-|
            return value => Expression.Lambda<Func<LambdaExpression>>(Expression.Call(value, factory)).Compile()();
        }

        static Func<Expression, LambdaExpression> DynamicLambdaFactory(string name, Type[] args, Type result)
        {
            return value =>
            {
                // retrieve actual target object, compiles every time and needs reflection too... :-(
                var targetObject = Expression.Lambda<Func<object>>(Expression.Convert(value, typeof(object))).Compile()();
                if (targetObject == null)
                    throw new InvalidOperationException($"Unable to retrieve object from '{value}': expression evaluates to null.");

                var target = targetObject.GetType();

                // actual method may provide different information
                var concreteMethod = target.GetRuntimeMethod(name, args);
                if (concreteMethod == null)
                    throw new InvalidOperationException($"Unable to retrieve lambda meta-data from {target.FullName}.{name}: what evil treachery is this?");

                var method = concreteMethod.Name;

                // configuration over convention, if any
                var metadata = concreteMethod.GetCustomAttribute<InjectLambdaAttribute>();
                if (!string.IsNullOrEmpty(metadata?.Method))
                    method = metadata.Method;

                // retrieve validated factory method
                var factory = FactoryMethod(target, method, args, result, true);

                // finally call lambda factory *uff*
                return (LambdaExpression)factory.Invoke(targetObject, null);
            };
        }

        static readonly Type[] emptyTypes = new Type[0];

        static MethodInfo FactoryMethod(Type target, string method, Type[] args, Type result, bool instance)
        {
            // assume method without any parameters
            var factory = target.GetRuntimeMethod(method, emptyTypes) ?? target.GetRuntimeProperty(method + "Expr")?.GetMethod();
            if (factory == null)
                throw new InvalidOperationException($"Unable to retrieve lambda expression from {target.FullName}.{method}: no parameterless member found.");

            // mixed static and instance methods?
            if (!instance && !factory.IsStatic)
                throw new InvalidOperationException($"Unable to retrieve lambda expression from {target.FullName}.{method}: static implementation expected.");
            if (instance && factory.IsStatic)
                throw new InvalidOperationException($"Unable to retrieve lambda expression from {target.FullName}.{method}: non-static implementation expected.");

            // method returns lambda expression?
            var returns = factory.ReturnType;
            if (!returns.IsConstructedGenericType() || returns.GetGenericTypeDefinition() != typeof(Expression<>))
                throw new InvalidOperationException($"Unable to retrieve lambda expression from {target.FullName}.{method}: method returns no lambda expression.");

            // lambda signature matches original method's signature?
            var signature = returns.GenericTypeArguments()[0].GetRuntimeMethod("Invoke", args);
            if (signature == null || signature.ReturnParameter.ParameterType != result)
                throw new InvalidOperationException($"Unable to retrieve lambda expression from {target.FullName}.{method}: method returns non-matching expression.");

            return factory;
        }
    }
}
