using System;
using SixLabors.ImageSharp; // Из одноимённого пакета NuGet
using SixLabors.ImageSharp.PixelFormats;
using System.Linq;
using SixLabors.ImageSharp.Processing;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.OnnxRuntime;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML.Transforms.Text;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;

namespace PredictorLibrary
{
    class ResultContext : DbContext
    {
        public DbSet<Result> SavedResults { get; set; }
        public DbSet<ImageData> Images { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder
                .UseLazyLoadingProxies()
                .UseSqlite("Data Source=E:/s02170150/WebLibrary/results.db");
    }

    public class ImageData
    {
        [Key]
        public int ImageDataId { get; set; }
        public byte[] Data { get; set; }
    }
    
    public class Result
    {
        [Key]
        public int ItemId { get; set; }
        public string Class { get; set; }
        public float Confidence { get; set; }
        public string Path { get; set; }
        public virtual ImageData Blob { get; set; }

        public override string ToString()
        {
            if (Path == null)
            {
                return Class;
            }
            return $"{Class} with confidence {Confidence} for image {Path}";
        }
    }

    public class Predictor
    {
        private string path_to_imgs;
        private string path_to_model;
        private int proc_count;
        private int counter;
        private int counter_max;
        private ConcurrentQueue<string> filenames;
        private InferenceSession session;
        private AutoResetEvent out_mutex;
        private ManualResetEvent cancel;
        private Result tmpResult;

        public delegate void Output(Result result);
        Output write;

        public Predictor(string path_to_imgs,
                         Output write,
                         string path_to_model = "E:\\s02170150\\PredictorLibrary\\resnet18-v1-7.onnx") //"..\\..\\..\\..\\PredictorLibrary\\resnet18-v1-7.onnx"
        {
            this.path_to_imgs = path_to_imgs;
            this.write += write;
            this.path_to_model = path_to_model;
            proc_count = Environment.ProcessorCount;
            session = new InferenceSession(path_to_model);
            out_mutex = new AutoResetEvent(true);
            cancel = new ManualResetEvent(false);
        }

        public string ImagePath
        {
            get
            {
                return path_to_imgs;
            }
            set
            {
                path_to_imgs = value;
            }
        }

        public void ProcessDirectory()
        {
            counter = 0;
            filenames ??= new ConcurrentQueue<string>();
            foreach (var path in  Directory.GetFiles(path_to_imgs, "*.jpeg"))
            {
                filenames.Enqueue(path);
            }
            counter_max = filenames.Count;
            out_mutex = new AutoResetEvent(true);
            cancel = new ManualResetEvent(false);

            Thread[] threads = new Thread[proc_count];
            for (int i = 0; i < proc_count; ++i)
            {
                threads[i] = new Thread(thread_method);
                threads[i].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }
        }

        public static void ClearDatabase()
        {
            Console.WriteLine("Clearing database");
            using (var db = new ResultContext())
            {
                foreach (var result in db.SavedResults)
                {
                    db.Remove(result);
                }

                db.SaveChanges();
            }
        }

        public static List<Result> GetDatabaseDir(string dir)
        {
            List<Result> ret = new List<Result>();
            using (var db = new ResultContext())
            {
                foreach (var path in Directory.GetFiles(dir, "*.jpeg"))
                {
                    Result saved = (from item in db.SavedResults.Include(a => a.Blob)
                        where item.Path == path
                        select item).First();
                    ret.Add(saved);
                }
            }
            Console.WriteLine("Returning from predictor");
            return ret;
        }
        
        public static Result[] ExtractDatabase()
        {
            Console.WriteLine("Extracting Database");
            
            using (var db = new ResultContext())
            {
                Result[] ret = new Result[db.SavedResults.Count()];
                int i = 0;
                foreach (var result in db.SavedResults.Include(a => a.Blob))
                {
                    ret[i] = result;
                    i += 1;
                }

                return ret;
            }
        }

        public static Result[] ExtractByClass(string classname)
        {
            Console.WriteLine("Extracting " + classname);

            using (var db = new ResultContext())
            {
                Result[] ret = new Result[db.SavedResults.Count(a => a.Class == classname)];
                var items = from item in db.SavedResults.Include(a=>a.Blob)
                        where item.Class == classname
                        select item;
                int i = 0;
                foreach (Result res in items)
                {
                    ret[i] = res;
                    i++;
                }
                return ret;
            }
        }

        public static string DatabaseStats()
        {
            string ret = "";
            using (var db = new ResultContext())
            {
                foreach (var classLabel in classLabels)
                {
                    int count = db.SavedResults.Count(a => a.Class == classLabel);
                    if (count > 0)
                    {
                        ret += $"{classLabel}: {count}\r\n";
                    }
                }
            }

            return ret;
        }

        public void Stop() => cancel.Set();

        private void thread_method()
        {
            string path;
            while (filenames.TryDequeue(out path))
            {
                if (cancel.WaitOne(0))
                {
                    write?.Invoke(new Result{ Class = "Interrupted", Blob = null, Confidence = 0.0f, Path = ""});
                    return;
                }

                IImageFormat format;
                using var image = Image.Load<Rgb24>(path, out format);

                using var ms = new MemoryStream();
                image.Save(ms, format); 
                byte[] blob = ms.ToArray();

                Result info;
                
                if (check_if_in_db(blob, path, out info))
                {
                    Console.WriteLine("Found identical: " + info);
                    write?.Invoke(info);
                    return;
                }

                Console.WriteLine("No identical found, processing");
                process_image(image, path, blob);
            }
        }

        private bool check_if_in_db(byte[] blob, string path, out Result info)
        {
            info = null;
            using var db = new ResultContext();
            var query = db.SavedResults.Where(a => a.Path == path);
            if (query.Count() == 0)
            {
                return false;
            }
            foreach (var result in query)
            {
                info = new Result{ Class = result.Class, Confidence = result.Confidence, Path = result.Path, Blob = new ImageData { Data = blob}};
                if (result.Blob.Data.Length != blob.Length)
                {
                    return false;
                }
                for (int i = 0; i < blob.Length; ++i)
                {
                    if (blob[i] != result.Blob.Data[i])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private void post_process(Result result)
        {
            counter += 1;
            using (var db = new ResultContext())
            {
                Console.WriteLine("Added new entity");
                db.Add(result);
                db.SaveChanges();
            }

            tmpResult = result;
        }

        public Result SaveAndProcessImage(string str)
        {
            var bytes = Convert.FromBase64String(str);
            string filename = "E:/s02170150/images/" + "tmp" + ".jpeg";
            using (var imageFile = new FileStream(filename, FileMode.Create))
            {
                imageFile.Write(bytes ,0, bytes.Length);
                imageFile.Flush();
            }
            
            filenames ??= new ConcurrentQueue<string>();
            filenames.Enqueue(filename);
            out_mutex = new AutoResetEvent(true);
            cancel = new ManualResetEvent(false);
            thread_method();

            return tmpResult;
        }
        
        private void process_image(Image<Rgb24> image, string path, byte[] blob)
        {

            const int targetHeight = 224;
            const int targetWidth = 224;

            image.Mutate(x =>
            {
                x.Resize(new ResizeOptions 
                { 
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Crop
                });
            });

            var input = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
            var mean = new[] { 0.485f, 0.456f, 0.406f };
            var stddev = new[] { 0.229f, 0.224f, 0.225f };
            for (int y = 0; y < targetHeight; y++)
            {
                Span<Rgb24> pixelSpan = image.GetPixelRowSpan(y);
                for (int x = 0; x < targetWidth; x++)
                {
                    input[0, 0, y, x] = ((pixelSpan[x].R / 255f) - mean[0]) / stddev[0];
                    input[0, 1, y, x] = ((pixelSpan[x].G / 255f) - mean[1]) / stddev[1];
                    input[0, 2, y, x] = ((pixelSpan[x].B / 255f) - mean[2]) / stddev[2];
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("data", input)
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);

            var output = results.First().AsEnumerable<float>().ToArray();
            var sum = output.Sum(x => (float)Math.Exp(x));
            var softmax = output.Select(x => (float)Math.Exp(x) / sum);

            foreach (var p in softmax
                .Select((x, i) => new { Label = classLabels[i], Confidence = x })
                .OrderByDescending(x => x.Confidence)
                .Take(1))
            {
                out_mutex.WaitOne(0);
                var result = new Result { Class = p.Label, Confidence = p.Confidence, Path = path, Blob = new ImageData { Data = blob } };
                write?.Invoke(result);
                post_process(result);
                out_mutex.Set();
            }
        }

        public override string ToString()
        {
            return $"Images: {path_to_imgs}; Model: {path_to_model}; Processors: {proc_count}.";
        }

        static readonly string[] classLabels = new[]
        {
            "tench",
            "goldfish",
            "great white shark",
            "tiger shark",
            "hammerhead shark",
            "electric ray",
            "stingray",
            "cock",
            "hen",
            "ostrich",
            "brambling",
            "goldfinch",
            "house finch",
            "junco",
            "indigo bunting",
            "American robin",
            "bulbul",
            "jay",
            "magpie",
            "chickadee",
            "American dipper",
            "kite",
            "bald eagle",
            "vulture",
            "great grey owl",
            "fire salamander",
            "smooth newt",
            "newt",
            "spotted salamander",
            "axolotl",
            "American bullfrog",
            "tree frog",
            "tailed frog",
            "loggerhead sea turtle",
            "leatherback sea turtle",
            "mud turtle",
            "terrapin",
            "box turtle",
            "banded gecko",
            "green iguana",
            "Carolina anole",
            "desert grassland whiptail lizard",
            "agama",
            "frilled-necked lizard",
            "alligator lizard",
            "Gila monster",
            "European green lizard",
            "chameleon",
            "Komodo dragon",
            "Nile crocodile",
            "American alligator",
            "triceratops",
            "worm snake",
            "ring-necked snake",
            "eastern hog-nosed snake",
            "smooth green snake",
            "kingsnake",
            "garter snake",
            "water snake",
            "vine snake",
            "night snake",
            "boa constrictor",
            "African rock python",
            "Indian cobra",
            "green mamba",
            "sea snake",
            "Saharan horned viper",
            "eastern diamondback rattlesnake",
            "sidewinder",
            "trilobite",
            "harvestman",
            "scorpion",
            "yellow garden spider",
            "barn spider",
            "European garden spider",
            "southern black widow",
            "tarantula",
            "wolf spider",
            "tick",
            "centipede",
            "black grouse",
            "ptarmigan",
            "ruffed grouse",
            "prairie grouse",
            "peacock",
            "quail",
            "partridge",
            "grey parrot",
            "macaw",
            "sulphur-crested cockatoo",
            "lorikeet",
            "coucal",
            "bee eater",
            "hornbill",
            "hummingbird",
            "jacamar",
            "toucan",
            "duck",
            "red-breasted merganser",
            "goose",
            "black swan",
            "tusker",
            "echidna",
            "platypus",
            "wallaby",
            "koala",
            "wombat",
            "jellyfish",
            "sea anemone",
            "brain coral",
            "flatworm",
            "nematode",
            "conch",
            "snail",
            "slug",
            "sea slug",
            "chiton",
            "chambered nautilus",
            "Dungeness crab",
            "rock crab",
            "fiddler crab",
            "red king crab",
            "American lobster",
            "spiny lobster",
            "crayfish",
            "hermit crab",
            "isopod",
            "white stork",
            "black stork",
            "spoonbill",
            "flamingo",
            "little blue heron",
            "great egret",
            "bittern",
            "crane (bird)",
            "limpkin",
            "common gallinule",
            "American coot",
            "bustard",
            "ruddy turnstone",
            "dunlin",
            "common redshank",
            "dowitcher",
            "oystercatcher",
            "pelican",
            "king penguin",
            "albatross",
            "grey whale",
            "killer whale",
            "dugong",
            "sea lion",
            "Chihuahua",
            "Japanese Chin",
            "Maltese",
            "Pekingese",
            "Shih Tzu",
            "King Charles Spaniel",
            "Papillon",
            "toy terrier",
            "Rhodesian Ridgeback",
            "Afghan Hound",
            "Basset Hound",
            "Beagle",
            "Bloodhound",
            "Bluetick Coonhound",
            "Black and Tan Coonhound",
            "Treeing Walker Coonhound",
            "English foxhound",
            "Redbone Coonhound",
            "borzoi",
            "Irish Wolfhound",
            "Italian Greyhound",
            "Whippet",
            "Ibizan Hound",
            "Norwegian Elkhound",
            "Otterhound",
            "Saluki",
            "Scottish Deerhound",
            "Weimaraner",
            "Staffordshire Bull Terrier",
            "American Staffordshire Terrier",
            "Bedlington Terrier",
            "Border Terrier",
            "Kerry Blue Terrier",
            "Irish Terrier",
            "Norfolk Terrier",
            "Norwich Terrier",
            "Yorkshire Terrier",
            "Wire Fox Terrier",
            "Lakeland Terrier",
            "Sealyham Terrier",
            "Airedale Terrier",
            "Cairn Terrier",
            "Australian Terrier",
            "Dandie Dinmont Terrier",
            "Boston Terrier",
            "Miniature Schnauzer",
            "Giant Schnauzer",
            "Standard Schnauzer",
            "Scottish Terrier",
            "Tibetan Terrier",
            "Australian Silky Terrier",
            "Soft-coated Wheaten Terrier",
            "West Highland White Terrier",
            "Lhasa Apso",
            "Flat-Coated Retriever",
            "Curly-coated Retriever",
            "Golden Retriever",
            "Labrador Retriever",
            "Chesapeake Bay Retriever",
            "German Shorthaired Pointer",
            "Vizsla",
            "English Setter",
            "Irish Setter",
            "Gordon Setter",
            "Brittany",
            "Clumber Spaniel",
            "English Springer Spaniel",
            "Welsh Springer Spaniel",
            "Cocker Spaniels",
            "Sussex Spaniel",
            "Irish Water Spaniel",
            "Kuvasz",
            "Schipperke",
            "Groenendael",
            "Malinois",
            "Briard",
            "Australian Kelpie",
            "Komondor",
            "Old English Sheepdog",
            "Shetland Sheepdog",
            "collie",
            "Border Collie",
            "Bouvier des Flandres",
            "Rottweiler",
            "German Shepherd Dog",
            "Dobermann",
            "Miniature Pinscher",
            "Greater Swiss Mountain Dog",
            "Bernese Mountain Dog",
            "Appenzeller Sennenhund",
            "Entlebucher Sennenhund",
            "Boxer",
            "Bullmastiff",
            "Tibetan Mastiff",
            "French Bulldog",
            "Great Dane",
            "St. Bernard",
            "husky",
            "Alaskan Malamute",
            "Siberian Husky",
            "Dalmatian",
            "Affenpinscher",
            "Basenji",
            "pug",
            "Leonberger",
            "Newfoundland",
            "Pyrenean Mountain Dog",
            "Samoyed",
            "Pomeranian",
            "Chow Chow",
            "Keeshond",
            "Griffon Bruxellois",
            "Pembroke Welsh Corgi",
            "Cardigan Welsh Corgi",
            "Toy Poodle",
            "Miniature Poodle",
            "Standard Poodle",
            "Mexican hairless dog",
            "grey wolf",
            "Alaskan tundra wolf",
            "red wolf",
            "coyote",
            "dingo",
            "dhole",
            "African wild dog",
            "hyena",
            "red fox",
            "kit fox",
            "Arctic fox",
            "grey fox",
            "tabby cat",
            "tiger cat",
            "Persian cat",
            "Siamese cat",
            "Egyptian Mau",
            "cougar",
            "lynx",
            "leopard",
            "snow leopard",
            "jaguar",
            "lion",
            "tiger",
            "cheetah",
            "brown bear",
            "American black bear",
            "polar bear",
            "sloth bear",
            "mongoose",
            "meerkat",
            "tiger beetle",
            "ladybug",
            "ground beetle",
            "longhorn beetle",
            "leaf beetle",
            "dung beetle",
            "rhinoceros beetle",
            "weevil",
            "fly",
            "bee",
            "ant",
            "grasshopper",
            "cricket",
            "stick insect",
            "cockroach",
            "mantis",
            "cicada",
            "leafhopper",
            "lacewing",
            "dragonfly",
            "damselfly",
            "red admiral",
            "ringlet",
            "monarch butterfly",
            "small white",
            "sulphur butterfly",
            "gossamer-winged butterfly",
            "starfish",
            "sea urchin",
            "sea cucumber",
            "cottontail rabbit",
            "hare",
            "Angora rabbit",
            "hamster",
            "porcupine",
            "fox squirrel",
            "marmot",
            "beaver",
            "guinea pig",
            "common sorrel",
            "zebra",
            "pig",
            "wild boar",
            "warthog",
            "hippopotamus",
            "ox",
            "water buffalo",
            "bison",
            "ram",
            "bighorn sheep",
            "Alpine ibex",
            "hartebeest",
            "impala",
            "gazelle",
            "dromedary",
            "llama",
            "weasel",
            "mink",
            "European polecat",
            "black-footed ferret",
            "otter",
            "skunk",
            "badger",
            "armadillo",
            "three-toed sloth",
            "orangutan",
            "gorilla",
            "chimpanzee",
            "gibbon",
            "siamang",
            "guenon",
            "patas monkey",
            "baboon",
            "macaque",
            "langur",
            "black-and-white colobus",
            "proboscis monkey",
            "marmoset",
            "white-headed capuchin",
            "howler monkey",
            "titi",
            "Geoffroy's spider monkey",
            "common squirrel monkey",
            "ring-tailed lemur",
            "indri",
            "Asian elephant",
            "African bush elephant",
            "red panda",
            "giant panda",
            "snoek",
            "eel",
            "coho salmon",
            "rock beauty",
            "clownfish",
            "sturgeon",
            "garfish",
            "lionfish",
            "pufferfish",
            "abacus",
            "abaya",
            "academic gown",
            "accordion",
            "acoustic guitar",
            "aircraft carrier",
            "airliner",
            "airship",
            "altar",
            "ambulance",
            "amphibious vehicle",
            "analog clock",
            "apiary",
            "apron",
            "waste container",
            "assault rifle",
            "backpack",
            "bakery",
            "balance beam",
            "balloon",
            "ballpoint pen",
            "Band-Aid",
            "banjo",
            "baluster",
            "barbell",
            "barber chair",
            "barbershop",
            "barn",
            "barometer",
            "barrel",
            "wheelbarrow",
            "baseball",
            "basketball",
            "bassinet",
            "bassoon",
            "swimming cap",
            "bath towel",
            "bathtub",
            "station wagon",
            "lighthouse",
            "beaker",
            "military cap",
            "beer bottle",
            "beer glass",
            "bell-cot",
            "bib",
            "tandem bicycle",
            "bikini",
            "ring binder",
            "binoculars",
            "birdhouse",
            "boathouse",
            "bobsleigh",
            "bolo tie",
            "poke bonnet",
            "bookcase",
            "bookstore",
            "bottle cap",
            "bow",
            "bow tie",
            "brass",
            "bra",
            "breakwater",
            "breastplate",
            "broom",
            "bucket",
            "buckle",
            "bulletproof vest",
            "high-speed train",
            "butcher shop",
            "taxicab",
            "cauldron",
            "candle",
            "cannon",
            "canoe",
            "can opener",
            "cardigan",
            "car mirror",
            "carousel",
            "tool kit",
            "carton",
            "car wheel",
            "automated teller machine",
            "cassette",
            "cassette player",
            "castle",
            "catamaran",
            "CD player",
            "cello",
            "mobile phone",
            "chain",
            "chain-link fence",
            "chain mail",
            "chainsaw",
            "chest",
            "chiffonier",
            "chime",
            "china cabinet",
            "Christmas stocking",
            "church",
            "movie theater",
            "cleaver",
            "cliff dwelling",
            "cloak",
            "clogs",
            "cocktail shaker",
            "coffee mug",
            "coffeemaker",
            "coil",
            "combination lock",
            "computer keyboard",
            "confectionery store",
            "container ship",
            "convertible",
            "corkscrew",
            "cornet",
            "cowboy boot",
            "cowboy hat",
            "cradle",
            "crane (machine)",
            "crash helmet",
            "crate",
            "infant bed",
            "Crock Pot",
            "croquet ball",
            "crutch",
            "cuirass",
            "dam",
            "desk",
            "desktop computer",
            "rotary dial telephone",
            "diaper",
            "digital clock",
            "digital watch",
            "dining table",
            "dishcloth",
            "dishwasher",
            "disc brake",
            "dock",
            "dog sled",
            "dome",
            "doormat",
            "drilling rig",
            "drum",
            "drumstick",
            "dumbbell",
            "Dutch oven",
            "electric fan",
            "electric guitar",
            "electric locomotive",
            "entertainment center",
            "envelope",
            "espresso machine",
            "face powder",
            "feather boa",
            "filing cabinet",
            "fireboat",
            "fire engine",
            "fire screen sheet",
            "flagpole",
            "flute",
            "folding chair",
            "football helmet",
            "forklift",
            "fountain",
            "fountain pen",
            "four-poster bed",
            "freight car",
            "French horn",
            "frying pan",
            "fur coat",
            "garbage truck",
            "gas mask",
            "gas pump",
            "goblet",
            "go-kart",
            "golf ball",
            "golf cart",
            "gondola",
            "gong",
            "gown",
            "grand piano",
            "greenhouse",
            "grille",
            "grocery store",
            "guillotine",
            "barrette",
            "hair spray",
            "half-track",
            "hammer",
            "hamper",
            "hair dryer",
            "hand-held computer",
            "handkerchief",
            "hard disk drive",
            "harmonica",
            "harp",
            "harvester",
            "hatchet",
            "holster",
            "home theater",
            "honeycomb",
            "hook",
            "hoop skirt",
            "horizontal bar",
            "horse-drawn vehicle",
            "hourglass",
            "iPod",
            "clothes iron",
            "jack-o'-lantern",
            "jeans",
            "jeep",
            "T-shirt",
            "jigsaw puzzle",
            "pulled rickshaw",
            "joystick",
            "kimono",
            "knee pad",
            "knot",
            "lab coat",
            "ladle",
            "lampshade",
            "laptop computer",
            "lawn mower",
            "lens cap",
            "paper knife",
            "library",
            "lifeboat",
            "lighter",
            "limousine",
            "ocean liner",
            "lipstick",
            "slip-on shoe",
            "lotion",
            "speaker",
            "loupe",
            "sawmill",
            "magnetic compass",
            "mail bag",
            "mailbox",
            "tights",
            "tank suit",
            "manhole cover",
            "maraca",
            "marimba",
            "mask",
            "match",
            "maypole",
            "maze",
            "measuring cup",
            "medicine chest",
            "megalith",
            "microphone",
            "microwave oven",
            "military uniform",
            "milk can",
            "minibus",
            "miniskirt",
            "minivan",
            "missile",
            "mitten",
            "mixing bowl",
            "mobile home",
            "Model T",
            "modem",
            "monastery",
            "monitor",
            "moped",
            "mortar",
            "square academic cap",
            "mosque",
            "mosquito net",
            "scooter",
            "mountain bike",
            "tent",
            "computer mouse",
            "mousetrap",
            "moving van",
            "muzzle",
            "nail",
            "neck brace",
            "necklace",
            "nipple",
            "notebook computer",
            "obelisk",
            "oboe",
            "ocarina",
            "odometer",
            "oil filter",
            "organ",
            "oscilloscope",
            "overskirt",
            "bullock cart",
            "oxygen mask",
            "packet",
            "paddle",
            "paddle wheel",
            "padlock",
            "paintbrush",
            "pajamas",
            "palace",
            "pan flute",
            "paper towel",
            "parachute",
            "parallel bars",
            "park bench",
            "parking meter",
            "passenger car",
            "patio",
            "payphone",
            "pedestal",
            "pencil case",
            "pencil sharpener",
            "perfume",
            "Petri dish",
            "photocopier",
            "plectrum",
            "Pickelhaube",
            "picket fence",
            "pickup truck",
            "pier",
            "piggy bank",
            "pill bottle",
            "pillow",
            "ping-pong ball",
            "pinwheel",
            "pirate ship",
            "pitcher",
            "hand plane",
            "planetarium",
            "plastic bag",
            "plate rack",
            "plow",
            "plunger",
            "Polaroid camera",
            "pole",
            "police van",
            "poncho",
            "billiard table",
            "soda bottle",
            "pot",
            "potter's wheel",
            "power drill",
            "prayer rug",
            "printer",
            "prison",
            "projectile",
            "projector",
            "hockey puck",
            "punching bag",
            "purse",
            "quill",
            "quilt",
            "race car",
            "racket",
            "radiator",
            "radio",
            "radio telescope",
            "rain barrel",
            "recreational vehicle",
            "reel",
            "reflex camera",
            "refrigerator",
            "remote control",
            "restaurant",
            "revolver",
            "rifle",
            "rocking chair",
            "rotisserie",
            "eraser",
            "rugby ball",
            "ruler",
            "running shoe",
            "safe",
            "safety pin",
            "salt shaker",
            "sandal",
            "sarong",
            "saxophone",
            "scabbard",
            "weighing scale",
            "school bus",
            "schooner",
            "scoreboard",
            "CRT screen",
            "screw",
            "screwdriver",
            "seat belt",
            "sewing machine",
            "shield",
            "shoe store",
            "shoji",
            "shopping basket",
            "shopping cart",
            "shovel",
            "shower cap",
            "shower curtain",
            "ski",
            "ski mask",
            "sleeping bag",
            "slide rule",
            "sliding door",
            "slot machine",
            "snorkel",
            "snowmobile",
            "snowplow",
            "soap dispenser",
            "soccer ball",
            "sock",
            "solar thermal collector",
            "sombrero",
            "soup bowl",
            "space bar",
            "space heater",
            "space shuttle",
            "spatula",
            "motorboat",
            "spider web",
            "spindle",
            "sports car",
            "spotlight",
            "stage",
            "steam locomotive",
            "through arch bridge",
            "steel drum",
            "stethoscope",
            "scarf",
            "stone wall",
            "stopwatch",
            "stove",
            "strainer",
            "tram",
            "stretcher",
            "couch",
            "stupa",
            "submarine",
            "suit",
            "sundial",
            "sunglass",
            "sunglasses",
            "sunscreen",
            "suspension bridge",
            "mop",
            "sweatshirt",
            "swimsuit",
            "swing",
            "switch",
            "syringe",
            "table lamp",
            "tank",
            "tape player",
            "teapot",
            "teddy bear",
            "television",
            "tennis ball",
            "thatched roof",
            "front curtain",
            "thimble",
            "threshing machine",
            "throne",
            "tile roof",
            "toaster",
            "tobacco shop",
            "toilet seat",
            "torch",
            "totem pole",
            "tow truck",
            "toy store",
            "tractor",
            "semi-trailer truck",
            "tray",
            "trench coat",
            "tricycle",
            "trimaran",
            "tripod",
            "triumphal arch",
            "trolleybus",
            "trombone",
            "tub",
            "turnstile",
            "typewriter keyboard",
            "umbrella",
            "unicycle",
            "upright piano",
            "vacuum cleaner",
            "vase",
            "vault",
            "velvet",
            "vending machine",
            "vestment",
            "viaduct",
            "violin",
            "volleyball",
            "waffle iron",
            "wall clock",
            "wallet",
            "wardrobe",
            "military aircraft",
            "sink",
            "washing machine",
            "water bottle",
            "water jug",
            "water tower",
            "whiskey jug",
            "whistle",
            "wig",
            "window screen",
            "window shade",
            "Windsor tie",
            "wine bottle",
            "wing",
            "wok",
            "wooden spoon",
            "wool",
            "split-rail fence",
            "shipwreck",
            "yawl",
            "yurt",
            "website",
            "comic book",
            "crossword",
            "traffic sign",
            "traffic light",
            "dust jacket",
            "menu",
            "plate",
            "guacamole",
            "consomme",
            "hot pot",
            "trifle",
            "ice cream",
            "ice pop",
            "baguette",
            "bagel",
            "pretzel",
            "cheeseburger",
            "hot dog",
            "mashed potato",
            "cabbage",
            "broccoli",
            "cauliflower",
            "zucchini",
            "spaghetti squash",
            "acorn squash",
            "butternut squash",
            "cucumber",
            "artichoke",
            "bell pepper",
            "cardoon",
            "mushroom",
            "Granny Smith",
            "strawberry",
            "orange",
            "lemon",
            "fig",
            "pineapple",
            "banana",
            "jackfruit",
            "custard apple",
            "pomegranate",
            "hay",
            "carbonara",
            "chocolate syrup",
            "dough",
            "meatloaf",
            "pizza",
            "pot pie",
            "burrito",
            "red wine",
            "espresso",
            "cup",
            "eggnog",
            "alp",
            "bubble",
            "cliff",
            "coral reef",
            "geyser",
            "lakeshore",
            "promontory",
            "shoal",
            "seashore",
            "valley",
            "volcano",
            "baseball player",
            "bridegroom",
            "scuba diver",
            "rapeseed",
            "daisy",
            "yellow lady's slipper",
            "corn",
            "acorn",
            "rose hip",
            "horse chestnut seed",
            "coral fungus",
            "agaric",
            "gyromitra",
            "stinkhorn mushroom",
            "earth star",
            "hen-of-the-woods",
            "bolete",
            "ear",
            "toilet paper"
        };
    }
}
