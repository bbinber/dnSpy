﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text.Editor;
using dnSpy.Properties;
using dnSpy.Text.Editor;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Output {
	interface IOutputServiceInternal : IOutputService {
		IInputElement FocusedElement { get; }
		bool CanCopy { get; }
		void Copy();
		bool CanClearAll { get; }
		void ClearAll();
		bool CanSaveText { get; }
		void SaveText();
		OutputBufferVM SelectLog(int index);
		bool CanSelectLog(int index);
		bool WordWrap { get; set; }
		bool ShowLineNumbers { get; set; }
		bool ShowTimestamps { get; set; }
		OutputBufferVM SelectedOutputBufferVM { get; }
		double ZoomLevel { get; }
	}

	[Export(typeof(IOutputServiceInternal)), Export(typeof(IOutputService))]
	sealed class OutputService : ViewModelBase, IOutputServiceInternal {
		public ICommand ClearAllCommand => new RelayCommand(a => ClearAll(), a => CanClearAll);
		public ICommand SaveCommand => new RelayCommand(a => SaveText(), a => CanSaveText);

		public bool WordWrap {
			get { return (outputServiceSettingsImpl.WordWrapStyle & WordWrapStyles.WordWrap) != 0; }
			set {
				if (WordWrap != value) {
					if (value)
						outputServiceSettingsImpl.WordWrapStyle |= WordWrapStyles.WordWrap;
					else
						outputServiceSettingsImpl.WordWrapStyle &= ~WordWrapStyles.WordWrap;
					OnPropertyChanged(nameof(WordWrap));
					foreach (var vm in OutputBuffers)
						vm.WordWrapStyle = outputServiceSettingsImpl.WordWrapStyle;
				}
			}
		}

		public bool ShowLineNumbers {
			get { return outputServiceSettingsImpl.ShowLineNumbers; }
			set {
				if (outputServiceSettingsImpl.ShowLineNumbers != value) {
					outputServiceSettingsImpl.ShowLineNumbers = value;
					OnPropertyChanged(nameof(ShowLineNumbers));
					foreach (var vm in OutputBuffers)
						vm.ShowLineNumbers = outputServiceSettingsImpl.ShowLineNumbers;
				}
			}
		}

		public bool ShowTimestamps {
			get { return outputServiceSettingsImpl.ShowTimestamps; }
			set {
				if (outputServiceSettingsImpl.ShowTimestamps != value) {
					outputServiceSettingsImpl.ShowTimestamps = value;
					OnPropertyChanged(nameof(ShowTimestamps));
					foreach (var vm in OutputBuffers)
						vm.ShowTimestamps = outputServiceSettingsImpl.ShowTimestamps;
				}
			}
		}

		public object TextEditorUIObject => SelectedOutputBufferVM?.TextEditorUIObject;
		public IInputElement FocusedElement => SelectedOutputBufferVM?.FocusedElement;
		public bool HasOutputWindows => SelectedOutputBufferVM != null;
		public double ZoomLevel => SelectedOutputBufferVM?.ZoomLevel ?? 100;

		public OutputBufferVM SelectedOutputBufferVM {
			get { return selectedOutputBufferVM; }
			set {
				if (selectedOutputBufferVM != value) {
					selectedOutputBufferVM = value;
					outputServiceSettingsImpl.SelectedGuid = value?.Guid ?? Guid.Empty;
					OnPropertyChanged(nameof(SelectedOutputBufferVM));
					OnPropertyChanged(nameof(TextEditorUIObject));
					OnPropertyChanged(nameof(FocusedElement));
					OnPropertyChanged(nameof(HasOutputWindows));
				}
			}
		}
		OutputBufferVM selectedOutputBufferVM;

		public ObservableCollection<OutputBufferVM> OutputBuffers => outputBuffers;
		readonly ObservableCollection<OutputBufferVM> outputBuffers;
		readonly ILogEditorProvider logEditorProvider;
		readonly OutputServiceSettingsImpl outputServiceSettingsImpl;
		readonly IPickSaveFilename pickSaveFilename;
		Guid prevSelectedGuid;
		readonly IEditorOperationsFactoryService editorOperationsFactoryService;
		readonly IMenuService menuService;

		[ImportingConstructor]
		OutputService(IEditorOperationsFactoryService editorOperationsFactoryService, ILogEditorProvider logEditorProvider, OutputServiceSettingsImpl outputServiceSettingsImpl, IPickSaveFilename pickSaveFilename, IMenuService menuService, [ImportMany] IEnumerable<Lazy<IOutputServiceListener, IOutputServiceListenerMetadata>> outputServiceListeners) {
			this.editorOperationsFactoryService = editorOperationsFactoryService;
			this.logEditorProvider = logEditorProvider;
			this.outputServiceSettingsImpl = outputServiceSettingsImpl;
			this.prevSelectedGuid = outputServiceSettingsImpl.SelectedGuid;
			this.pickSaveFilename = pickSaveFilename;
			this.menuService = menuService;
			this.outputBuffers = new ObservableCollection<OutputBufferVM>();
			this.outputBuffers.CollectionChanged += OutputBuffers_CollectionChanged;

			var listeners = outputServiceListeners.OrderBy(a => a.Metadata.Order).ToArray();
			Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => {
				foreach (var lazy in outputServiceListeners) {
					var l = lazy.Value;
				}
			}));
		}

		void OutputBuffers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
			if (SelectedOutputBufferVM == null)
				SelectedOutputBufferVM = OutputBuffers.FirstOrDefault();

			if (e.NewItems != null) {
				foreach (OutputBufferVM vm in e.NewItems) {
					vm.WordWrapStyle = outputServiceSettingsImpl.WordWrapStyle;
					vm.ShowLineNumbers = outputServiceSettingsImpl.ShowLineNumbers;
					vm.ShowTimestamps = outputServiceSettingsImpl.ShowTimestamps;
					if (vm.Guid == prevSelectedGuid && prevSelectedGuid != Guid.Empty) {
						SelectedOutputBufferVM = vm;
						prevSelectedGuid = Guid.Empty;
					}
				}
			}
		}

		public IOutputTextPane Create(Guid guid, string name, string contentType) =>
			Create(guid, name, (object)contentType);

		public IOutputTextPane Create(Guid guid, string name, IContentType contentType) =>
			Create(guid, name, (object)contentType);

		IOutputTextPane Create(Guid guid, string name, object contentTypeObj) {
			if (name == null)
				throw new ArgumentNullException(nameof(name));

			var vm = OutputBuffers.FirstOrDefault(a => a.Guid == guid);
			Debug.Assert(vm == null || vm.Name == name);
			if (vm != null)
				return vm;

			var logEditorOptions = new LogEditorOptions {
				MenuGuid = new Guid(MenuConstants.GUIDOBJ_LOG_TEXTEDITORCONTROL_GUID),
				ContentType = contentTypeObj as IContentType,
				ContentTypeString = contentTypeObj as string,
				CreateGuidObjects = args => CreateGuidObjects(args),
			};
			logEditorOptions.ExtraRoles.Add(PredefinedDsTextViewRoles.OutputTextPane);
			var logEditor = logEditorProvider.Create(logEditorOptions);
			logEditor.TextView.Options.SetOptionValue(DefaultWpfViewOptions.AppearanceCategory, Constants.Output);

			// Prevent toolwindow's ctx menu from showing up when right-clicking in the left margin
			menuService.InitializeContextMenu(logEditor.TextViewHost.HostControl, Guid.NewGuid());

			vm = new OutputBufferVM(editorOperationsFactoryService, guid, name, logEditor);
			int index = GetSortedInsertIndex(vm);
			OutputBuffers.Insert(index, vm);
			while (index < OutputBuffers.Count)
				OutputBuffers[index].Index = index++;

			OutputTextPaneUtils.AddInstance(vm, logEditor.TextView);
			return vm;
		}

		IEnumerable<GuidObject> CreateGuidObjects(GuidObjectsProviderArgs args) {
			yield return new GuidObject(MenuConstants.GUIDOBJ_OUTPUT_MANAGER_GUID, this);
			var vm = SelectedOutputBufferVM as IOutputTextPane;
			if (vm != null)
				yield return new GuidObject(MenuConstants.GUIDOBJ_ACTIVE_OUTPUT_TEXTPANE_GUID, vm);
		}

		int GetSortedInsertIndex(OutputBufferVM vm) {
			for (int i = 0; i < OutputBuffers.Count; i++) {
				if (StringComparer.InvariantCultureIgnoreCase.Compare(vm.Name, OutputBuffers[i].Name) < 0)
					return i;
			}
			return OutputBuffers.Count;
		}

		public IOutputTextPane Find(Guid guid) => OutputBuffers.FirstOrDefault(a => a.Guid == guid);
		public IOutputTextPane GetTextPane(Guid guid) => Find(guid) ?? new NotPresentOutputWriter(this, guid);

		public void Select(Guid guid) {
			var vm = OutputBuffers.FirstOrDefault(a => a.Guid == guid);
			Debug.Assert(vm != null);
			if (vm != null)
				this.SelectedOutputBufferVM = vm;
		}

		public bool CanCopy => SelectedOutputBufferVM?.CanCopy == true;
		public void Copy() => SelectedOutputBufferVM?.Copy();

		public bool CanClearAll => SelectedOutputBufferVM != null;

		public void ClearAll() {
			if (!CanClearAll)
				return;
			SelectedOutputBufferVM?.Clear();
		}

		public bool CanSaveText => SelectedOutputBufferVM != null;

		public void SaveText() {
			if (!CanSaveText)
				return;
			var vm = SelectedOutputBufferVM;
			var filename = pickSaveFilename.GetFilename(GetFilename(vm), "txt", TEXTFILES_FILTER);
			if (filename == null)
				return;
			try {
				File.WriteAllText(filename, vm.GetText());
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex);
			}
		}
		static readonly string TEXTFILES_FILTER = string.Format("{1} (*.txt)|*.txt|{0} (*.*)|*.*", dnSpy_Resources.AllFiles, dnSpy_Resources.TextFiles);

		string GetFilename(OutputBufferVM vm) {
			// Same as VS2015
			var s = vm.Name.Replace(" ", string.Empty);
			return dnSpy_Resources.Window_Output + "-" + s + ".txt";
		}

		public bool CanSelectLog(int index) => (uint)index < (uint)OutputBuffers.Count;

		public OutputBufferVM SelectLog(int index) {
			if (!CanSelectLog(index))
				return null;
			SelectedOutputBufferVM = OutputBuffers[index];
			return SelectedOutputBufferVM;
		}
	}
}