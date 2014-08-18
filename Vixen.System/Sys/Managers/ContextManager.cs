﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Vixen.Execution;
using Vixen.Execution.Context;
using Vixen.Sys.Attribute;
using Vixen.Sys.Instrumentation;

namespace Vixen.Sys.Managers
{
	public class ContextManager : IEnumerable<IContext>
	{
		private static NLog.Logger Logging = NLog.LogManager.GetCurrentClassLogger();
		private ConcurrentDictionary<Guid, IContext> _instances;
		private MillisecondsValue _contextUpdateTimeValue = new MillisecondsValue("   Contexts update");
		private MillisecondsValue _contextUpdateWaitValue = new MillisecondsValue("   Contexts wait");
		private Stopwatch _stopwatch = Stopwatch.StartNew();
		private LiveContext _systemLiveContext;
		private HashSet<Guid> _affectedElements = new HashSet<Guid>(); 

		public event EventHandler<ContextEventArgs> ContextCreated;
		public event EventHandler<ContextEventArgs> ContextReleased;

		public ContextManager()
		{
			_instances = new ConcurrentDictionary<Guid, IContext>();
			VixenSystem.Instrumentation.AddValue(_contextUpdateTimeValue);
			//VixenSystem.Instrumentation.AddValue(_contextUpdateWaitValue);
		}

		public LiveContext GetSystemLiveContext()
		{
			if (_systemLiveContext == null) {
				_systemLiveContext = new LiveContext("System");
				_AddContext(_systemLiveContext);
			}
			return _systemLiveContext;
		}

		public IProgramContext CreateProgramContext(ContextFeatures contextFeatures, IProgram program)
		{
			return CreateProgramContext(contextFeatures, program, new ProgramExecutor(program));
		}

		public IProgramContext CreateProgramContext(ContextFeatures contextFeatures, IProgram program, IExecutor executor)
		{
			if (contextFeatures == null) throw new ArgumentNullException("contextFeatures");
			if (executor == null) throw new ArgumentNullException("executor");

			IProgramContext context = (IProgramContext) _CreateContext(ContextTargetType.Program, contextFeatures);
			if (context != null) {
				context.Executor = executor;
				context.Program = program;
				_AddContext(context);
			}
			return context;
		}

		public ISequenceContext CreateSequenceContext(ContextFeatures contextFeatures, ISequence sequence)
		{
			ISequenceExecutor executor = Vixen.Services.SequenceTypeService.Instance.CreateSequenceExecutor(sequence);
			ISequenceContext context = (ISequenceContext) _CreateContext(ContextTargetType.Sequence, contextFeatures);
			if (executor != null && context != null) {
				context.Executor = executor;
				context.Sequence = sequence;
				_AddContext(context);
			}
			return context;
		}

		public void ReleaseContext(IContext context)
		{
			if (_instances.ContainsKey(context.Id)) {
				_ReleaseContext(context);
			}
		}

		public void ReleaseContexts()
		{
			foreach (IContext context in _instances.Values.ToArray()) {
				ReleaseContext(context);
			}
			lock (_instances) {
				_instances.Clear();
			}
		}

		public HashSet<Guid> Update()
		{
			_stopwatch.Restart();
			affectedElements.Clear();
			foreach( var context in _instances.Values.Where(x => x.IsRunning))
			{
				try
				{
					// Get a snapshot time value for this update.
					TimeSpan contextTime = context.GetTimeSnapshot();
					var contextAffectedElements = context.UpdateElementStates(contextTime);
					if (contextAffectedElements != null)
					{
						//Aggregate the effected elements so we can do more selective work downstream
						_affectedElements.AddRange(contextAffectedElements);
					}

				}
				catch (Exception ee)
				{
					Logging.ErrorException(ee.Message, ee);
				}
			}
			
			_contextUpdateTimeValue.Set(_stopwatch.ElapsedMilliseconds);
			return _affectedElements;
		}

		public IEnumerator<IContext> GetEnumerator()
		{
			IContext[] contexts;
			//lock (_instances) {
				contexts = _instances.Values.ToArray();
			//}
	 
			return contexts.Cast<IContext>().GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		private IContext _CreateContext(ContextTargetType contextTargetType, ContextFeatures contextFeatures)
		{
			if (contextFeatures == null) throw new ArgumentNullException("contextFeatures");
			Type contextType = _FindContextWithFeatures(contextTargetType, contextFeatures);
			if (contextType == null) {
				Logging.Error(string.Format("Could not find a context for target type {0} with features {1}", contextTargetType, contextFeatures));
				return null;
			}
			return (ContextBase) Activator.CreateInstance(contextType);
		}

		private Type _FindContextWithFeatures(ContextTargetType contextTargetType, ContextFeatures contextFeatures)
		{
			IEnumerable<Type> contextTypes = Assembly.GetExecutingAssembly().GetAttributedTypes(typeof (ContextAttribute));
			return
				contextTypes.FirstOrDefault(
					x =>
					x.GetCustomAttributes(typeof(ContextAttribute), false).Cast<ContextAttribute>().Any(
						y => y.TargetType == contextTargetType && y.Caching == contextFeatures.Caching));
		}

		private void _AddContext(IContext context)
		{
			lock (_instances) {
				_instances[context.Id] = context;
			}
			OnContextCreated(new ContextEventArgs(context));
		}

		private void _ReleaseContext(IContext context)
		{
			context.Stop();
			//lock (_instances)
			//{
				IContext remval = null;
				_instances.TryRemove(context.Id, out remval);
			//	_instances.Remove(context.Id);
			//}
			context.Dispose();
			OnContextReleased(new ContextEventArgs(context));
		}

		protected virtual void OnContextCreated(ContextEventArgs e)
		{
			if (ContextCreated != null) {
				ContextCreated(this, e);
			}
		}

		protected virtual void OnContextReleased(ContextEventArgs e)
		{
			if (ContextReleased != null) {
				ContextReleased(this, e);
			}
		}
	}
}