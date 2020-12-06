using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PredictorLibrary;

namespace UI
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        //private Predictor pred = null;
        private HttpClient _httpClient;
        private static CancellationTokenSource cts = new CancellationTokenSource();
        private string url = "http://localhost:5000/server";
        
        private ObservableCollection<ClassName> class_nums;
        private ObservableCollection<Image> images;
        private ObservableCollection<Result> results;
        private ConcurrentQueue<string> image_filenames;
        private ObservableCollection<Image> filtered_imgs;
        private ICollectionView list_classes_updater;
        private Thread[] threads;

        public void Output(Result result)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                results.Add(result);
                var q = from i in class_nums
                        where i.Class == result.Class
                        select i;
                if (q.Count() == 0)
                {
                    class_nums.Add(new ClassName(result.Class));
                }
                else
                {
                    q.First().Num++;
                    list_classes_updater.Refresh();
                }
                
                for (int i = 0; i < images.Count; ++i)
                {
                    if (images[i].Path == result.Path)
                    {
                        images[i].Class = result.Class;
                        break;
                    }
                }
            }));
        }

        public MainWindow()
        {
            InitializeComponent();
            _httpClient = new HttpClient();
            threads = new Thread[Environment.ProcessorCount];
            for (int i = 0; i < Environment.ProcessorCount; ++i)
            {
                threads[i] = null;
            }
            setBindings();
            Extract();
        }

        private void setBindings()
        {
            images = new ObservableCollection<Image>();
            results = new ObservableCollection<Result>();
            class_nums = new ObservableCollection<ClassName>();
            filtered_imgs = new ObservableCollection<Image>();

            Binding img2list = new Binding();
            img2list.Source = images;
            list_box_images.SetBinding(ItemsControl.ItemsSourceProperty, img2list);

            Binding numresults = new Binding();
            numresults.Source = results;
            numresults.Path = new PropertyPath("Count");
            num_results.SetBinding(TextBlock.TextProperty, numresults);

            Binding class_num = new Binding();
            class_num.Source = class_nums;
            list_box_classes.SetBinding(ItemsControl.ItemsSourceProperty, class_num);
            list_classes_updater = CollectionViewSource.GetDefaultView(list_box_classes.ItemsSource);

            Binding filtered = new Binding();
            filtered.Source = filtered_imgs;
            list_box_selected_imgs.SetBinding(ItemsControl.ItemsSourceProperty, filtered);
        }

        private async void Extract()
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url + "/results");
            }
            catch (HttpRequestException)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("Connection failed.", "ERROR");
                }));
                return;
            }
            Console.WriteLine("Here");

            if (response.IsSuccessStatusCode)
            {
                Result[] results = JsonConvert.DeserializeObject<Result[]>(response.Content.ReadAsStringAsync().Result);
                foreach (var result in results)
                {
                    await Dispatcher.BeginInvoke(new Action(() => 
                    {
                        Image image = new Image(result.Blob.Data);
                        image.Path = result.Path;
                        images.Add(image);
                    }));
                    Output(result);
                }
            }
            else
            {
                Console.WriteLine(response.ToString());
            }
        }
        
        private void thread_method()
        {
            string path;
            while (image_filenames.TryDequeue(out path))
            {
                string close_path = path;
                Dispatcher.BeginInvoke(new Action(() => 
                {
                    images.Add(new Image(close_path));
                }));
            }
        }

        private async void Open(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.SelectedPath = "E:\\s02170150\\images";
                    System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                    if (result != System.Windows.Forms.DialogResult.OK) return;
                    
                    foreach (Thread t in threads)
                    {
                        t?.Join();
                    }
                    image_filenames = new ConcurrentQueue<string>(Directory.GetFiles(dialog.SelectedPath, "*.jpeg"));
                    results.Clear();
                    images.Clear();
                    class_nums.Clear();
                    
                    // Test change

                    for (int i = 0; i < Environment.ProcessorCount; ++i)
                    {
                        threads[i] = new Thread(thread_method);
                        threads[i].Start();
                    }

                    var content = new StringContent(JsonConvert.SerializeObject(dialog.SelectedPath), Encoding.UTF8, "application/json");
                    HttpResponseMessage response;
                    try
                    {
                        response = await _httpClient.PostAsync(url, content, cts.Token);
                    }
                    catch (HttpRequestException)
                    {
                        await Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show("Connection failed.", "ERROR");
                        }));
                        return;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        List<Result> ret =
                            JsonConvert.DeserializeObject<List<Result>>(response.Content.ReadAsStringAsync().Result);

                        foreach (var res in ret)
                        {
                            Output(res);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void Stop(object sender, RoutedEventArgs e)
        {
            cts.Cancel(false);
            cts.Dispose();
            cts = new CancellationTokenSource();
        }

        private void list_box_classes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            filtered_imgs.Clear();
            ClassName selected = list_box_classes.SelectedItem as ClassName;
            if (selected == null) return;
            foreach (var t in images)
            {
                if (t.Class == selected.Class)
                {
                    filtered_imgs.Add(t);
                }
            }
        }

        private async void Clear_DB(object sender, RoutedEventArgs e)
        {
            HttpResponseMessage response;
            try
            {
                response = _httpClient.DeleteAsync(url).Result;
            }
            catch (HttpRequestException)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("Connection failed.", "ERROR");
                }));
                return;
            }
        }

        private async void Stats(object sender, RoutedEventArgs e)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url + "/dbstats");
            }
            catch (HttpRequestException)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("Connection failed.", "ERROR");
                }));
                return;
            }
            
            if (response.IsSuccessStatusCode)
            {
                string stats = response.Content.ReadAsStringAsync().Result;
                
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show(stats, "Stats");
                }));
            }
        }
    }

    public class ClassName : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public string Class { get; set; }
        private int num;
        public int Num
        {
            get
            {
                return num;
            }
            set
            {
                num = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Num"));
            }
        }

        public ClassName(string class_name)
        {
            Num = 1;
            Class = class_name;
        }

        public override string ToString()
        {
            return Class + ": " + Num;
        }
    }

    public class Image : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public string Path { get; set; }
        private string class_name;
        public string Class 
        {
            get
            {
                return class_name;
            }
            set
            {
                class_name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Class"));
            }
        }
        public BitmapImage Bitmap { get; set; }

        public Image(string path)
        {
            Path = path;
            if (path == null)
            {
                Console.WriteLine("Error here");
            }
            Bitmap = new BitmapImage(new Uri(path));
            Class = "";
        }

        public Image(byte[] blob)
        {
            using (var ms = new System.IO.MemoryStream(blob))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // here
                image.StreamSource = ms;
                image.EndInit();
                Bitmap = image;
            }
        }
    }
}
