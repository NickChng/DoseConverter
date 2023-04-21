using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EQD2Converter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        private int SelectedIndex;
        private bool DragSelected = false;

        public MainWindow(ViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer scv = (ScrollViewer)sender;
            scv.ScrollToVerticalOffset((scv.VerticalOffset - e.Delta/10));
            e.Handled = true;
        }
        private double ConvertTextToDouble(string text)
        {
            if (Double.TryParse(text, out double result))
            {
                return result;
            }
            else
            {
                return Double.NaN;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox txt = sender as TextBox;
            int ind = txt.CaretIndex;
            txt.Text = txt.Text.Replace(",", ".");
            txt.CaretIndex = ind;
        }
        private void Constraint_ListView_MouseLeave(object sender, MouseEventArgs e)
        {
            // This method disables drag after the mouse leaves the listbox, so user needs to click on drag icon again to restart
            // However, if the mouse doen't leave the listbox, drag can still be accomplished by clicking the listboxitem

            var LV = sender as ListView; // all this to get the listbox!
            if (LV.AllowDrop && Mouse.LeftButton == MouseButtonState.Released)
            {
                LV.AllowDrop = false;
                DragSelected = false; // this needs to be at protocol level as it's used to suppress selection/expansion of constraint in XAML
            }
        }
        private void DragConstraint_MouseDown(object sender, MouseButtonEventArgs e)
        {
            StructureListView.AllowDrop = true;
            DragSelected = true;

        }
        delegate Point GetPositionDelegate(IInputElement element);
        private void DragConstraint_ListView_DragOver(object sender, DragEventArgs e)
        {
            var VM = DataContext as ViewModel;
            var SelectedIndex = DragConstraint_GetCurrentIndex(e.GetPosition);
            //

            var DropIndex = VM.SelectedIndex;
            if (SelectedIndex < 0)
                return;
            if (DropIndex < 0)
                return;
            if (SelectedIndex == DropIndex)
                return;
            if (DragSelected)
            {
                //int OldIndex = VM.AlphaBetaMappings[SelectedIndex].DisplayOrder;
                int inc = 1;
                if (SelectedIndex < DropIndex)
                    inc = -1;
                int CurrentIndex = DropIndex;
                while (CurrentIndex != SelectedIndex)
                {
                    //int Switch = VM.AlphaBetaMappings[CurrentIndex + inc].DisplayOrder;
                    //VM.AlphaBetaMappings[CurrentIndex + inc].DisplayOrder = VM.TemplateStructures[CurrentIndex].DisplayOrder;
                    //VM.AlphaBetaMappings[CurrentIndex].DisplayOrder = Switch;
                    VM.AlphaBetaMappings.Move(CurrentIndex + inc, CurrentIndex);
                    CurrentIndex = CurrentIndex + inc;
                }

            }
        }
        ListViewItem GetListViewItem(int index, ListView LV)
        {
            if (LV.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                return null;

            return LV.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
        }
        bool IsMouseOverTarget(Visual target, GetPositionDelegate getPosition)
        {
            Rect bounds = VisualTreeHelper.GetDescendantBounds(target);
            Point mousePos = getPosition((IInputElement)target);
            return bounds.Contains(mousePos);
        }
        int DragConstraint_GetCurrentIndex(GetPositionDelegate getPosition)
        {
            int index = -1;
            for (int i = 0; i < StructureListView.Items.Count; ++i)
            {
                ListViewItem item = GetListViewItem(i, StructureListView);
                if (item != null)
                    if (this.IsMouseOverTarget(item, getPosition))
                    {
                        index = i;
                        break;
                    }
            }
            return index;
        }
        private void DragConstraint_Drop(object sender, DragEventArgs e)
        {
            var LV = sender as ListView; // all this to get the listbox!
            if (LV.AllowDrop && Mouse.LeftButton == MouseButtonState.Released)
            {
                LV.AllowDrop = false;
                DragSelected = false; 
            }
        }



    }
}