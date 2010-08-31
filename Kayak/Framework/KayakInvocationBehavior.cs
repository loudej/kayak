﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using LitJson;
using System.IO;

namespace Kayak.Framework
{
    // not thread-safe!
    public class KayakInvocationBehavior : IInvocationBehavior
    {
        public Func<IKayakContext, MethodInfo> Map { get; set; }
        public List<IInvocationArgumentBinder> Binders { get; private set; }
        public List<IInvocationResultHandler> ResultHandlers { get; private set; }
        public List<IInvocationExceptionHandler> ExceptionHandlers { get; private set; }

        static string InvocationResultContextKey = "InvocationResultContextKey";

        public KayakInvocationBehavior()
        {
            Binders = new List<IInvocationArgumentBinder>();
            ResultHandlers = new List<IInvocationResultHandler>();
            ExceptionHandlers = new List<IInvocationExceptionHandler>();
        }

        public IObservable<InvocationInfo> Bind(IKayakContext context)
        {
            return BindInternal(context).AsCoroutine<InvocationInfo>();
        }

        IEnumerable<object> BindInternal(IKayakContext context)
        {
            InvocationInfo info = new InvocationInfo();

            info.Method = Map(context);

            if (info.Method == null)
                yield break;

            info.Target = Activator.CreateInstance(info.Method.DeclaringType);

            var service = info.Target as KayakService;

            if (service != null)
                service.Context = context;

            var parameterCount = info.Method.GetParameters().Length;
            info.Arguments = new object[parameterCount];

            if (parameterCount > 0)
                foreach (var binder in Binders)
                {
                    binder.BindArgumentsFromHeaders(context, info);
                    yield return binder.BindArgumentsFromBody(context, info);
                }

            yield return info;
        }

        public void Invoke(IKayakContext context, IObservable<object> invocation)
        {
            invocation.Subscribe(
                result => InvocationResult(context, result), 
                e => InvocationException(context, e), 
                () => InvocationCompleted(context));
        }

        void InvocationCompleted(IKayakContext context)
        {
            if (!context.Items.ContainsKey(InvocationResultContextKey))
            {
                context.End();
                return;
            }

            object result = context.Items[InvocationResultContextKey];

            if (result is IEnumerable<object>)
            {
                object lastObject = null;

                (result as IEnumerable<object>)
                    .AsCoroutine<object>()
                    .Subscribe(o => { lastObject = o; }, e => InvocationException(context, e), () => HandleResult(context, lastObject));
            }
            else
                HandleResult(context, result);
        }

        void InvocationException(IKayakContext context, Exception exception)
        {
            var handler = GetExceptionHandler(context, exception);

            if (handler != null)
                handler.Subscribe(
                    u => { }, 
                    e => { }, 
                    () => context.End());
            else
                context.End();
        }

        void InvocationResult(IKayakContext context, object result)
        {
            context.Items[InvocationResultContextKey] = result;
        }

        void HandleResult(IKayakContext context, object result)
        {
            var handler = GetResultHandler(context, result);

            if (handler != null)
                handler.Subscribe(u => { }, e => InvocationException(context, e), () => context.End());
            else
                context.End();
        }

        // this could be made public to support Comet-style shit.
        // Context.Return(somevalue) { Context.GetBehavior().GetResultHandler(Context, somevalue); }
        IObservable<Unit> GetResultHandler(IKayakContext context, object result)
        {
            var info = context.GetInvocationInfo();

            foreach (var resultHandler in ResultHandlers)
            {
                var h = resultHandler.HandleResult(context, info, result);

                if (h != null)
                    return h;
            }

            return null;
        }

        IObservable<Unit> GetExceptionHandler(IKayakContext context, Exception e)
        {
            var info = context.GetInvocationInfo();

            foreach (var exceptionHandler in ExceptionHandlers)
            {
                var h = exceptionHandler.HandleException(context, info, e);

                if (h != null)
                    return h;
            }

            return null;
        }
    }
}
