using System;
using System.Collections.Generic;
using System.IO;

namespace d77Xtract
{
    class Program
    {
        public struct record
        {
            public byte[] filename;
            public byte type;
            public byte pos;
            public byte size;
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
                throw new Exception("need a .d77 image as arg");
            string sourcefile = args[0];
            using (BinaryReader br = new BinaryReader(File.Open(sourcefile, FileMode.Open)))
            {
                string source = Path.GetFileNameWithoutExtension(sourcefile);
                string dirName = source+"/Raw";
                br.BaseStream.Position = 0x1c;
                uint filesize = br.ReadUInt32();
                uint[] tracks = new uint[164];
                for (int i = 0; i < 164; i++)
                {
                    tracks[i] = br.ReadUInt32();
                }
                //prep the dir
                if (!Directory.Exists(dirName))
                    Directory.CreateDirectory(dirName);
                //and now that they're loaded up, let's navigate the sectors!
                int usedSize = 0;
                int readSize = 0x2B0;
                for (int i = 0; i < 164; i++) //only the first two tracks (two heads each) are reserved, let's copy all sectors of this out
                    if (tracks[i] != 0)
                    {
                        br.BaseStream.Position = (int)tracks[i];
                        //now for each sector...
                        int secs = 0;
                        while (true)
                        {
                            byte t = br.ReadByte();//ring
                            byte h = br.ReadByte();//head
                            byte s = br.ReadByte();//sector
                            byte numSecs = br.ReadByte();//num of records this sector
                            ushort secTotal = br.ReadUInt16();//total records this track
                            br.BaseStream.Position += 8;
                            ushort secSize = br.ReadUInt16();//size of this record!
                            readSize += 0x10;
                            string filename = "T" + t + "H" + h + "S" + s;
                            /*if (t == 1 && h == 0)
                            {
                                if (s == 4)
                                    filename = "directory";
                                else if (s == 1)
                                    filename = "FAT";
                            }*/
                            using (BinaryWriter bw = new BinaryWriter(File.Create(dirName + "/" + filename)))
                            {
                                for (int b = 0; b < secSize; b++)
                                    bw.Write(br.ReadByte());
                                secs+= numSecs;
                                if (secs >= secTotal)
                                    break;
                            }
                        }

                    }
                //now let's try and detect used sectors in others
                /*for(int i = 4; i < tracks.Length; i++)
                {
                    //check for E5 and FF
                    br.BaseStream.Position = (int)tracks[i];
                    br.ReadBytes(3);
                    byte numSecs = br.ReadByte();//num of records this sector
                    ushort secTotal = br.ReadUInt16();//total records this track
                    br.ReadBytes(0x08);
                    ushort secSize = br.ReadUInt16();
                    bool copy = false;
                    for(int b = 0; b < secSize; b++)
                    {
                        byte v = br.ReadByte();
                        if(v != 0xE5 && v != 0xFF)
                        {
                            copy = true;
                            break;
                        }    
                    }
                    if(copy)
                    {
                        br.BaseStream.Position = (int)tracks[i];
                        byte t = br.ReadByte();//ring
                        byte h = br.ReadByte();//head
                        byte s = br.ReadByte();//sector
                        br.ReadBytes(0x0D);
                        using (BinaryWriter bw = new BinaryWriter(File.Create(dirName + "/T" + t + "H" + h + "S" + s)))
                            for (int b = 0; b < secSize; b++)
                            {
                                bw.Write(br.ReadByte());
                            }

                    }
                }*/
                //now, open the directory and the FAT itself to pull the files out!
                List<record> records = new List<record>();
                int dirpage = 0;
                using (BinaryReader FAT = new BinaryReader(File.OpenRead(dirName + "/T1H0S1"))) //FAT table
                {
                    BinaryReader dir = new BinaryReader(File.OpenRead(dirName + "/T1H0S4")); //dictionary
                    while (true)
                    {
                        if (dir.BaseStream.Position == dir.BaseStream.Length)
                        {
                            dirpage++;
                            dir = new BinaryReader(File.OpenRead(dirName + "/T1H0S" + (4 + dirpage)));
                        }
                        record r = new record();
                        r.filename = dir.ReadBytes(8);
                        dir.ReadBytes(3);
                        r.type = dir.ReadByte();
                        if (r.type == 0xFF)
                            break;
                        dir.ReadBytes(2);
                        byte p = dir.ReadByte();
                        dir.ReadBytes(17); //seek to the next entry
                        if (p == 0xFF)
                            break;
                        r.pos = p; //the total sector offset!
                        FAT.BaseStream.Position = 5 + p;
                        //follow the number chain until you hit a CX number
                        byte l = 0;
                        while(true)
                        {
                            byte b = FAT.ReadByte();
                            if(b >= 0xC0)
                            {
                                l += (byte)(b & 0x3F);
                                l++; //extra
                                break;
                            }
                            else
                            {
                                l += 8;
                                FAT.BaseStream.Position = 5 + b;
                            }
                        }
                        r.size = l; //total sector size count of the file
                        records.Add(r);
                    }
                }
                string outDir = source+"/Files";
                if (!Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);
                //and now, time to actually copy out buffers!
                for (int r = 0; r < records.Count; r++)
                {
                    using (BinaryWriter fw = new BinaryWriter(File.Create(outDir + "/" + System.Text.Encoding.UTF8.GetString(records[r].filename))))
                    {
                        br.BaseStream.Position = 0x20 + (records[r].pos / 2 + 4) * 4;
                        uint trackPos = br.ReadUInt32();
                        int t = 0;
                        //now we're at the start of the track containing our file
                        //what head do we need to start on? what record?
                        int rec = 1 + (records[r].pos & 0x1)*8;
                        for (int secs = 0; secs < records[r].size; secs++)
                        {
                            br.BaseStream.Position = trackPos;
                            while (true)
                            {
                                br.ReadByte();//track
                                br.ReadByte();//head
                                byte s = br.ReadByte();//record
                                if (s != rec + secs)
                                {
                                    br.ReadBytes(11);
                                    br.ReadBytes(br.ReadUInt16());//advance to the next record
                                    continue;
                                }
                                //else, this is our record
                                br.ReadBytes(11);
                                ushort tlen = br.ReadUInt16();
                                for (int b = 0; b < tlen; b++)
                                    fw.Write(br.ReadByte());
                                //now we need the next sector
                                break;
                            }
                            if(rec+secs == 0x10)
                            {
                                //uh oh, we need to go to the next track!
                                t++;
                                br.BaseStream.Position = 0x20 + (records[r].pos / 2 + 4 + t) * 4;
                                trackPos = br.ReadUInt32();
                                rec -= 0x10;
                            }
                        }
                    }
                }

                Console.WriteLine("File Size: " + filesize.ToString("X"));
                Console.WriteLine("Read bytes: " + readSize.ToString("X"));
                Console.WriteLine("Used bytes: " + usedSize.ToString("X"));
            }
        }
    }
}
