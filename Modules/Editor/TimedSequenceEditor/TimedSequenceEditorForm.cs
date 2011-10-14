﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Vixen.Execution;
using Vixen.Sys;
using Vixen.Module.Editor;
using Vixen.Module.Sequence;
using Vixen.Module.Effect;
using Vixen.Module.EffectEditor;
using Vixen.Module.Property;
using Vixen.Module.Timing;
using CommonElements.Timeline;
using VixenModules.Sequence.Timed;
using System.Diagnostics;
using System.IO;

namespace VixenModules.Editor.TimedSequenceEditor
{
	public partial class TimedSequenceEditorForm : Form, IEditorUserInterface
	{
		#region Member Variables

		private TimedSequence _sequence;
		private ProgramContext _context;
		private ITiming _timingSource;
		private Dictionary<EffectNode, TimelineElement> _effectNodeToElement;
		private Dictionary<ChannelNode, List<TimelineRow>> _channelNodeToRows;

		#endregion



		public TimedSequenceEditorForm()
		{
			InitializeComponent();

			timelineControl.ElementChangedRows += ElementChangedRowsHandler;
			timelineControl.ElementDoubleClicked += ElementDoubleClickedHandler;

			LoadAvailableEffects();

			// JRR drag/drop
			timelineControl.DataDropped += timelineControl_DataDropped;
		}


		private void LoadAvailableEffects()
		{
			foreach (IEffectModuleDescriptor effectDesriptor in ApplicationServices.GetModuleDescriptors<IEffectModuleInstance>().Cast<IEffectModuleDescriptor>()) {
				// Add an entry to the menu
				ToolStripMenuItem menuItem = new ToolStripMenuItem(effectDesriptor.EffectName);
				menuItem.Tag = effectDesriptor.TypeId;
				menuItem.Click += (sender, e) => {
					// TODO
				};
				addEffectToolStripMenuItem.DropDownItems.Add(menuItem);

				// Add a button to the tool strip
				ToolStripItem tsItem = new ToolStripButton(effectDesriptor.EffectName);
				tsItem.Tag = effectDesriptor.TypeId;
				tsItem.MouseDown += toolStripEffects_Item_MouseDown;
				toolStripEffects.Items.Add(tsItem);
			}
		}


	
		#region IEditorUserInterface implementation

		// TODO: what is this supposed to be for?
		public EditorValues EditorValues
		{
			get { throw new NotImplementedException(); }
		}

		public bool IsModified { get; private set; }

		public void NewSequence()
		{
			Vixen.Sys.ISequence newSequence = new TimedSequence();
			newSequence.Length = TimeSpan.FromMinutes(1);
			Sequence = newSequence;

			// TODO: do other stuff here like setting default zooms, clearing snaps, etc.
		}

		public void RefreshSequence()
		{
			Sequence = Sequence;
		}

		public void Save(string filePath = null)
		{
			SaveSequence(filePath);
		}

		public ISelection Selection
		{
			get { throw new NotImplementedException(); }
		}

		public Vixen.Sys.ISequence Sequence
		{
			get { return _sequence; }
			set
			{
				LoadSequence(value);
			}
		}

		public IEditorModuleInstance OwnerModule { get; set; }

		public void Start()
		{
			Show();
		}

		#endregion






		#region Saving / Loading Methods

		/// <summary>
		/// Loads all nodes (groups/channels) currently in the system as rows in the timeline control.
		/// </summary>
		private void LoadSystemNodesToRows(bool clearCurrentRows = true)
		{
			_channelNodeToRows = new Dictionary<ChannelNode, List<TimelineRow>>();

			if (clearCurrentRows)
				timelineControl.ClearAllRows();

			foreach (ChannelNode node in Vixen.Sys.Execution.Nodes.RootNodes) {
				AddNodeAsRow(node, null);
			}
		}

		/// <summary>
		/// Adds a single given channel node as a row in the timeline control. Recursively adds all
		/// child nodes of the given node as children, if needed.
		/// </summary>
		/// <param name="node">The node to generate a row for.</param>
		/// <param name="parentRow">The parent node the row should belong to, if any.</param>
		private void AddNodeAsRow(ChannelNode node, TimelineRow parentRow)
		{
			// made the new row from the given node and add it to the control.
			TimelineRow newRow = timelineControl.AddRow(node.Name, parentRow);
			newRow.ElementRemoved += ElementRemovedFromRowHandler;
			newRow.ElementAdded += ElementAddedToRowHandler;

			// Tag it with the node it refers to, and take note of which row the given channel node will refer to.
			newRow.Tag = node;
			if (_channelNodeToRows.ContainsKey(node))
				_channelNodeToRows[node].Add(newRow);
			else
				_channelNodeToRows[node] = new List<TimelineRow> { newRow };

			// iterate through all if its children, adding them as needed
			foreach (ChannelNode child in node.Children) {
				AddNodeAsRow(child, newRow);
			}
		}

		private void LoadSequence(Vixen.Sys.ISequence sequence)
		{
			// check if it's the right type of sequence that we know how to deal with. If not, complain bitterly.
			if (sequence is TimedSequence)
				_sequence = (TimedSequence)sequence;
			else {
				throw new NotImplementedException("Cannot use sequence type with a Timed Sequence Editor");
			}

			timelineControl.TotalTime = _sequence.Length;

			// update our program context with this sequence
			OpenSequenceContext(sequence);

			// clear out all the old data
			LoadSystemNodesToRows();

			// load the new data: get all the commands in the sequence, and make a new element for each of them.
			_effectNodeToElement = new Dictionary<EffectNode, TimelineElement>();
			foreach (EffectNode node in _sequence.Data.GetEffects()) {
				AddElementForEffectNode(node);
			}

			IsModified = false;
		}

		/// <summary>
		/// Populates the TimelineControl grid with a new TimedSequenceElement for the given EffectNode.
		/// Will add a single TimedSequenceElement to in each row that each targeted channel of
		/// the EffectNode references. It will also add callbacks to event handlers for the element.
		/// </summary>
		/// <param name="node">The EffectNode to make element(s) in the grid for.</param>
		private void AddElementForEffectNode(EffectNode node)
		{
			TimedSequenceElement element = null;
			// for the effect, make a single element and add it to every row that represents its target channels
			foreach (ChannelNode target in node.Effect.TargetNodes) {
				if (_channelNodeToRows.ContainsKey(target)) {
					List<TimelineRow> targetRows = _channelNodeToRows[target];
					// make a new element for each row that represents the channel this command is in.
					foreach (TimelineRow row in targetRows) {
						if (element == null) {
							element = new TimedSequenceElement(node);
							element.ElementContentChanged += ElementContentChangedHandler;
							element.ElementMoved += ElementMovedHandler;

							if (!_effectNodeToElement.ContainsKey(node))
								_effectNodeToElement[node] = element;
							else
								VixenSystem.Logging.Debug("TimedSequenceEditor: Making a new element, but the map already has one!");
						}

						row.AddElement(element);
					}
				} else {
					// we don't have a row for the channel this effect is referencing; most likely, the row has
					// been deleted, or we're opening someone else's sequence, etc. Big fat TODO: here for that, then.
					// dunno what we want to do: prompt to add new channels for them? map them to others? etc.
				}
			}
		}

		/// <summary>
		/// Removes the TimedSequenceElement in the TimelineControl that refer to the given effect node.
		/// </summary>
		/// <param name="node">The EffectNode to purge the element in the grid for.</param>
		private void RemoveElementForEffectNode(EffectNode node)
		{
			TimelineElement element = _effectNodeToElement[node];
			// iterate through all rows, trying to remove the element that the given effect is represented by
			foreach (TimelineRow row in timelineControl) {
				if (row.ContainsElement(element)) {
					row.RemoveElement(element);
				}
			}

			element.ElementContentChanged -= ElementContentChangedHandler;
			element.ElementMoved -= ElementMovedHandler;
			_effectNodeToElement.Remove(node);
		}

		/// <summary>
		/// Saves the current sequence to a file. May prompt for a file name to save the sequence to if needed.
		/// </summary>
		/// <param name="filePath">The filename to save the sequence to. If null, the filename in the sequence will be used.
		/// If that is also null, the user will be prompted for a filename.</param>
		/// <param name="forcePrompt">If true, the user will always be prompted for a filename to save the sequence to.</param>
		private void SaveSequence(string filePath = null, bool forcePrompt = false)
		{
			if (_sequence == null) {
				VixenSystem.Logging.Error("Trying to save a sequence that is null!");
			}

			if (filePath == null | forcePrompt) {
				if (_sequence.FilePath.Trim() == "" || forcePrompt) {
					CommonElements.TextDialog prompt = new CommonElements.TextDialog("Please enter a sequence name:");
					prompt.ShowDialog();
					string extension = Path.GetExtension(prompt.Response);
					string name = Path.GetFileNameWithoutExtension(prompt.Response);

					if (name.Trim() != "") {
						// if the given extension isn't valid for this type, then keep the name intact and add an extension
						// TODO: should we pick one type? Should an editor even be able to edit multiple file types? etc...
						if (!((OwnerModule.Descriptor) as TimedSequenceEditorDescriptor).FileExtensions.Contains(extension)) {
							name = name + extension;
							extension = ((OwnerModule.Descriptor) as TimedSequenceEditorDescriptor).FileExtensions.First();
						}

						_sequence.Save(name + extension);
					} else {
						VixenSystem.Logging.Info("No name given for sequence on save, not saving.");
						return;
					}
				} else {
					_sequence.Save();
				}
			} else {
				_sequence.Save(filePath);
			}

			IsModified = false;
		}

		#endregion


		#region Event Handlers

		protected void ElementContentChangedHandler(object sender, EventArgs e)
		{
			TimedSequenceElement element = sender as TimedSequenceElement;
			// TODO: I'm not sure if we will need to do anything here; if we are updating effect details,
			// will the EffectEditors configure the EffectNode object directly?
			IsModified = true;
		}

		protected void ElementMovedHandler(object sender, EventArgs e)
		{
			// update the effect this element represents
			TimedSequenceElement element = sender as TimedSequenceElement;
			element.EffectNode.StartTime = element.StartTime;
			element.EffectNode.Effect.TimeSpan = element.Duration;
			IsModified = true;
		}

		protected void ElementRemovedFromRowHandler(object sender, ElementEventArgs e)
		{
			// not currently used
		}

		protected void ElementAddedToRowHandler(object sender, ElementEventArgs e)
		{
			// not currently used
		}

		protected void ElementChangedRowsHandler(object sender, ElementRowChangeEventArgs e)
		{
			ChannelNode oldChannel = e.OldRow.Tag as ChannelNode;
			ChannelNode newChannel = e.NewRow.Tag as ChannelNode;
			TimedSequenceElement movedElement = e.Element as TimedSequenceElement;
			//List<TimelineElement> targetElements = _effectNodeToElement[movedElement.EffectNode];

			// retarget the selected element from the old channel it was in to the new channel it was dragged to
			// TODO: there's *got* to be a better way of adding/removing a single item from an array...
			List<ChannelNode> nodeList = new List<ChannelNode>(movedElement.EffectNode.Effect.TargetNodes);
			if (nodeList.Contains(oldChannel)) {
				nodeList.Remove(oldChannel);
			} else {
				VixenSystem.Logging.Debug("TimedSequenceEditor: moving an element from " + e.OldRow.Name +
					" to " + e.NewRow.Name + "and the effect element wasn't in the old row channel!");
			}
			nodeList.Add(newChannel);
			movedElement.EffectNode.Effect.TargetNodes = nodeList.ToArray();

			// now that the effect that this element has been updated to accurately reflect the change,
			// move the actual element around. It's a single element in the grid, belonging to multiple rows:
			// so find all rows that represent the old channel, remove the element from them, and also find
			// all rows that represent the new channel and add it to them.
			foreach (TimelineRow row in timelineControl) {
				ChannelNode rowChannel = row.Tag as ChannelNode;

				if (rowChannel == oldChannel && row != e.OldRow)
					row.RemoveElement(movedElement);
				if (rowChannel == newChannel && row != e.NewRow)
					row.AddElement(movedElement);
			}

			IsModified = true;
		}

		protected void ElementDoubleClickedHandler(object sender, ElementEventArgs e)
		{
			TimedSequenceElement element = e.Element as TimedSequenceElement;

			if (element.EffectNode == null) {
				VixenSystem.Logging.Error("TimedSequenceEditor: Element double-clicked, and it doesn't have an associated effect!");
				return;
			}

			using (TimedSequenceEditorEffectEditor editor = new TimedSequenceEditorEffectEditor(element.EffectNode)) {
				DialogResult result = editor.ShowDialog();
			}
		}

		#endregion





		#region Sequence actions (play, pause, etc.)

		private void OpenSequenceContext(Vixen.Sys.ISequence sequence)
		{
			if (_context != null) {
				CloseSequenceContext();
			}
			_context = Execution.CreateContext(Sequence);
			_context.SequenceStarted += context_SequenceStarted;
			_context.SequenceEnded += context_SequenceEnded;
		}

		private void CloseSequenceContext()
		{
			_context.SequenceStarted -= context_SequenceStarted;
			_context.SequenceEnded -= context_SequenceEnded;
			Execution.ReleaseContext(_context);
		}

		public void PlaySequence()
		{
			if (_context == null) {
				VixenSystem.Logging.Error("TimedSequenceEditor: attempt to Play with null context!");
				return;
			}

			_context.Play();
		}

		public void PauseSequence()
		{
			if (_context == null) {
				VixenSystem.Logging.Error("TimedSequenceEditor: attempt to Play with null context!");
				return;
			}

			_context.Pause();
		}

		public void StopSequence()
		{
			if (_context == null) {
				VixenSystem.Logging.Error("TimedSequenceEditor: attempt to Play with null context!");
				return;
			}

			_context.Stop();
		}

		void context_SequenceStarted(object sender, SequenceStartedEventArgs e)
		{
			timerPlaying.Start();
			_timingSource = e.TimingSource;
		}

		void context_SequenceEnded(object sender, SequenceEventArgs e)
		{
			timerPlaying.Stop();
			_timingSource = null;
		}

		private void timerPlaying_Tick(object sender, EventArgs e)
		{
			if (_timingSource != null)
				toolStripStatusLabel_currentTime.Text = _timingSource.Position.ToString("m\\:ss\\.fff");
		}

		#endregion




		#region Tool Strip Menu Items

		private void toolStripMenuItem_Save_Click(object sender, EventArgs e)
		{
			SaveSequence();
		}

		private void toolStripMenuItem_SaveAs_Click(object sender, EventArgs e)
		{
			SaveSequence(null, true);
		}

		private void toolStripMenuItem_Close_Click(object sender, EventArgs e)
		{
			Close();
		}


		private void toolStripButton_Play_Click(object sender, EventArgs e)
		{
			PlaySequence();
		}

		private void toolStripButton_Stop_Click(object sender, EventArgs e)
		{
			StopSequence();
		}

		private void toolStripButton_Pause_Click(object sender, EventArgs e)
		{
			PauseSequence();
		}


		#endregion


		/// <summary>
		/// Adds an effect to the sequence and timelineControl.
		/// </summary>
		private void addNewEffect(Guid effectId, TimelineRow row, TimeSpan startTime, TimeSpan timeSpan)
		{
			try {
				// get the target channel
				ChannelNode targetNode = (ChannelNode)row.Tag;

				// get a new instance of this effect, populate it, and make a node for it
				IEffectModuleInstance effect = Vixen.Sys.ApplicationServices.Get<IEffectModuleInstance>(effectId);
				effect.TargetNodes = new ChannelNode[] { targetNode };
				effect.TimeSpan = timeSpan;
				EffectNode effectNode = new EffectNode(effect, startTime);

				// put it in the sequence and in the timeline display
				_sequence.InsertData(effectNode);
				AddElementForEffectNode(effectNode);
				IsModified = true;
			} catch (Exception ex) {
				VixenSystem.Logging.Error("TimedSequenceEditor: error adding effect of type " + effectId + " to row " + row.Name, ex);
			}
		}



		#region Effect Drag/Drop

		void toolStripEffects_Item_MouseDown(object sender, MouseEventArgs e)
		{
			ToolStripItem item = sender as ToolStripItem;

			DataObject data = new DataObject(DataFormats.Serializable, item.Tag);
			item.GetCurrentParent().DoDragDrop(data, DragDropEffects.Copy);
		}

		void timelineControl_DataDropped(object sender, TimelineDropEventArgs e)
		{
			Guid effectGuid = (Guid)e.Data.GetData(DataFormats.Serializable);
			TimeSpan timeSpan = TimeSpan.FromSeconds(2.0);	// TODO: need a default value here. I suggest a per-effect default.
			addNewEffect(effectGuid, e.Row, e.Time, timeSpan);
		}

		#endregion


		private void TimedSequenceEditorForm_FormClosed(object sender, FormClosedEventArgs e)
		{
			Execution.ReleaseContext(_context);
		}

	}
}