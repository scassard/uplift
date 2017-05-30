using System;
using System.IO;
using System.Xml.Serialization;

namespace Schemas
{
     public partial class Upbring
    {

        private static string[] upbringPathDefinition = {"Assets", "upackages", "Upbring.xml"};
        protected static string upbringPath {
            get { 
                return String.Join(System.IO.Path.DirectorySeparatorChar.ToString(), upbringPathDefinition);
            }
        }
        public static Upbring FromXml() {
            if(!File.Exists(upbringPath)) {
                Upbring newUpbring = new Schemas.Upbring();
                newUpbring.InstalledPackage = new InstalledPackage[0];
                return newUpbring;
            }
            XmlSerializer serializer = new XmlSerializer(typeof(Schemas.Upbring));
            FileStream fs = new FileStream(upbringPath, FileMode.Open);
            Upbring upbringFile =  serializer.Deserialize(fs) as Schemas.Upbring;
            fs.Close();
            return upbringFile;
        }

        public void SaveFile() {
            XmlSerializer serializer = new XmlSerializer(typeof(Schemas.Upbring));
            FileStream fs = new FileStream(upbringPath, FileMode.Create);
            StreamWriter sw = new StreamWriter(fs, System.Text.Encoding.UTF8);
		    serializer.Serialize(sw, this);
            sw.Close();
            fs.Close();
        }

        public static void RemoveFile() {
            File.Delete(upbringPath);
        }
        public void RemovePackage() {
            throw new NotImplementedException();
        }

        internal void AddPackage(Upset package)
        {
            InstalledPackage newPackage = new InstalledPackage();
            newPackage.Name = package.PackageName;
            newPackage.Version = package.PackageVersion;

            InstallationSpecs newPackageInstallPlace = new InstallationSpecs();
            newPackageInstallPlace.Kind = Schemas.KindSpec.Base;
            newPackageInstallPlace.Path = LocalHandler.GetLocalDirectory(package.PackageName, package.PackageVersion);
            newPackage.Install = new InstallationSpecs[]{newPackageInstallPlace};
            
            for(var i = 0; i < this.InstalledPackage.Length; i++) {
                InstalledPackage ip = this.InstalledPackage[i];
                if(ip.Name == newPackage.Name) {
                    this.InstalledPackage[i] = newPackage;
                    return;
                }
            }

            InstalledPackage[] finalArray = new InstalledPackage[this.InstalledPackage.Length + 1];
            this.InstalledPackage.CopyTo(finalArray, 0);
            finalArray[this.InstalledPackage.Length] = newPackage;

            this.InstalledPackage = finalArray;
        }


    }
}