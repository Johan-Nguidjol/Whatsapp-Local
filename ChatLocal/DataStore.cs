using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ChatLocal
{
    public class ContactData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsArchived { get; set; }
        public string ProfilePhotoData { get; set; }
        public int EphemeralSeconds { get; set; }
    }

    public class CommunityData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> MemberIds { get; set; } = new List<string>();
        public int UnreadCount { get; set; }
    }

    public class StoreData
    {
        public List<ContactData> Contacts { get; set; } = new List<ContactData>();
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        public List<StatusPost> Statuses { get; set; } = new List<StatusPost>();
        public List<CommunityData> Communities { get; set; } = new List<CommunityData>();
        public bool NotificationSoundsEnabled { get; set; } = true;
        public string ThemeColor { get; set; } = "Green";
        public string Language { get; set; } = "fr";
    }

    public class ProfileData
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class DataStore
    {
        private readonly string _dataPath;
        private readonly string _backupPath;
        private readonly string _profilePhotoPath;
        private readonly object _lock = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public string UserId { get; }
        public string UserName { get; }
        public string DataDirectory { get; }

        public DataStore(string name)
        {
            UserName = name;
            string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "user";

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChatLocal", safe);
            Directory.CreateDirectory(dir);
            DataDirectory = dir;

            string profilePath = Path.Combine(dir, "profile.json");
            string id = null;
            try
            {
                if (File.Exists(profilePath))
                    id = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(profilePath))?.Id;
            }
            catch { }

            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                File.WriteAllText(profilePath, JsonSerializer.Serialize(new ProfileData { Id = id, Name = name }));
            }

            UserId = id;
            _dataPath = Path.Combine(dir, "data.json");
            _backupPath = _dataPath + ".bak";
            _profilePhotoPath = Path.Combine(dir, "profile-photo.jpg");
        }

        public byte[] LoadProfilePhoto()
        {
            try { return File.Exists(_profilePhotoPath) ? File.ReadAllBytes(_profilePhotoPath) : null; }
            catch { return null; }
        }

        public void SaveProfilePhoto(byte[] imageBytes)
        {
            lock (_lock)
            {
                string tempPath = _profilePhotoPath + ".tmp";
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(imageBytes, 0, imageBytes.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(_profilePhotoPath))
                        File.Replace(tempPath, _profilePhotoPath, _profilePhotoPath + ".bak", true);
                    else
                        File.Move(tempPath, _profilePhotoPath);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw new IOException("Impossible d’enregistrer la photo dans « " + DataDirectory + " ».");
                }
            }
        }

        public void RemoveProfilePhoto()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_profilePhotoPath)) File.Delete(_profilePhotoPath);
                    if (File.Exists(_profilePhotoPath + ".bak")) File.Delete(_profilePhotoPath + ".bak");
                }
                catch { throw new IOException("Impossible de supprimer la photo de profil."); }
            }
        }

        public StoreData Load()
        {
            var data = TryLoad(_dataPath);
            if (data != null) return data;

            // A failed or interrupted save must not make the next save erase history.
            data = TryLoad(_backupPath);
            if (data != null) return data;
            return new StoreData();
        }

        private static StoreData TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        public void Save(StoreData data)
        {
            lock (_lock)
            {
                string tempPath = _dataPath + ".tmp";
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    byte[] payload = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOptions));
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush(true);
                    }

                    if (File.Exists(_dataPath))
                    {
                        // File.Replace is atomic on the same volume and preserves the previous version.
                        File.Replace(tempPath, _dataPath, _backupPath, true);
                    }
                    else
                    {
                        File.Move(tempPath, _dataPath);
                    }
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw new IOException("Impossible d’enregistrer les données dans « " + DataDirectory + " ». Vérifiez l’espace disque et les autorisations.");
                }
            }
        }
    }
}
