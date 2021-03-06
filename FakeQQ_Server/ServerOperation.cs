﻿//此类实现：服务端在接收到数据包后做出不同操作

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web;
using System.Web.Script.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Collections;

namespace FakeQQ_Server
{
    class ServerOperation
    {
        private string DataSourceName = "C418";
        private string AdministratorID;
        private Socket server;
        bool serverIsRunning = false;
        public ArrayList onlineUserList = new ArrayList();

        public delegate void CrossThreadCallControlHandler(object sender, EventArgs e);
        public static event CrossThreadCallControlHandler UpdateOnlineUserList;
        public static event CrossThreadCallControlHandler AdministratorModifyPassword;
        public static void ToUpdateOnlineUserList(object sender, EventArgs e)
        {
            Console.WriteLine("one user login");
            UpdateOnlineUserList?.Invoke(sender, e);
        }
        public static void ToAdministratorModifyPassword(object sender, EventArgs e)
        {
            AdministratorModifyPassword?.Invoke(sender, e);
        }
        public ServerOperation(string AdministratorID)
        {
            this.AdministratorID = AdministratorID;
        }
        //启动服务
        public bool StartServer()
        {
            bool success = false;
            try
            {
                //定义IP地址
                IPAddress local = IPAddress.Parse("127.0.0.1");
                int port = 8500;
                IPEndPoint iep = new IPEndPoint(local, port);
                //创建服务器的socket对象
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(iep);
                server.Listen(20);
                server.BeginAccept(new AsyncCallback(AcceptCallback), server);
                success = true;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString());
                success = false;
            }
            serverIsRunning = true;
            return success;
        }

        //关闭服务
        public bool CloseServer()
        {
            server.Close();
            for(int i=0; i<onlineUserList.Count; i++)
            {
                ((UserIDAndSocket)onlineUserList[i]).Service.Close();
            }
            serverIsRunning = false;
            return true;
        }

        //监测客户端是否掉线
        public void CheckOnlineUserList(object sender, System.Timers.ElapsedEventArgs e)
        {
            Console.WriteLine("onlineUserList.Count = " + onlineUserList.Count);
            if (onlineUserList.Count > 0)
            {
                //遍历在线用户表，找出上次接收心跳包的时间和当前时间的距离大于预定义的值的用户
                ArrayList offlineUserID = new ArrayList();
                for(int i=0; i<onlineUserList.Count; i++)
                {
                    if((DateTime.Now - ((UserIDAndSocket)onlineUserList[i]).LastHeartBeatTime).Seconds > 3)
                    {
                        Console.WriteLine("a user is offline");
                        offlineUserID.Add(((UserIDAndSocket)onlineUserList[i]).UserID);
                    }
                }
                //遍历离线用户表和在线用户表
                //如果在线用户表里面的某个用户ID也存在于离线用户表，关闭和这个用户连接的service，从在线用户列表里面删除此用户
                for(int i=0; i<offlineUserID.Count; i++)
                {
                    for(int j=0; j<onlineUserList.Count; j++)
                    {
                        if((string)offlineUserID[i] == ((UserIDAndSocket)onlineUserList[j]).UserID)
                        {
                            ((UserIDAndSocket)onlineUserList[j]).Service.Close();
                            onlineUserList.RemoveAt(j);
                            break;
                        }
                    }
                    //对于每一个刚刚离线的用户，向他们所有的好友发送下线信息
                    SqlConnection conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                    SqlCommand cmd = new SqlCommand("select FriendID from dbo.Friends where ID='" + offlineUserID[i] + "'", conn);
                    if (conn.State == ConnectionState.Closed)
                    {
                        try
                        {
                            conn.Open();
                            SqlDataReader DataReader = cmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                            while (DataReader.Read())
                            {
                                string friendID = DataReader["FriendID"].ToString();
                                //构造要发送的数据包
                                DataPacket packet = new DataPacket();
                                packet.ComputerName = "server";
                                packet.NameLength = packet.ComputerName.Length;
                                packet.FromIP = IPAddress.Parse("0.0.0.0");
                                packet.ToIP = IPAddress.Parse("0.0.0.0");
                                packet.CommandNo = 27;
                                packet.Content = (string)offlineUserID[i];
                                //发送！
                                for(int k=0; i<onlineUserList.Count; i++)
                                {
                                    if(friendID == ((UserIDAndSocket)onlineUserList[k]).UserID)
                                    {
                                        Send(((UserIDAndSocket)onlineUserList[k]).Service, packet.PacketToBytes());
                                    }
                                }
                            }
                            DataReader.Close();
                        }
                        catch
                        {
                            Console.WriteLine("发布离线消息时出错！");
                        }
                        finally
                        {
                            conn.Close();
                        }
                    }
                }
                ToUpdateOnlineUserList(null, null);
            }
        }

        //发布系统消息
        public void ReleaseSystemMessage(string message)
        {
            //构造数据包
            DataPacket packet = new DataPacket();
            packet.CommandNo = 24;
            packet.Content = message;
            packet.ComputerName = "server";
            packet.NameLength = packet.ComputerName.Length;
            packet.FromIP = IPAddress.Parse("0.0.0.0");
            packet.ToIP = IPAddress.Parse("0.0.0.0");
            //将该数据包发送给所有在线用户
            for(int i=0; i<onlineUserList.Count; i++)
            {
                Send(((UserIDAndSocket)onlineUserList[i]).Service, packet.PacketToBytes());
            }
        }

        //管理员修改自己的密码
        public void ChangeAdministratorPassword(string oldPassword, string newPassword)
        {
            Console.WriteLine("Administrator " + AdministratorID + "want to modify its password");
            bool Correct = false;
            SqlConnection conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
            SqlCommand cmd = new SqlCommand("select Password from dbo.Administrator where AdministratorID='" + AdministratorID + "'", conn);
            if (conn.State == ConnectionState.Closed)
            {
                try
                {
                    conn.Open();
                    SqlDataReader DataReader = cmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                    while (DataReader.Read())
                    {
                        if (oldPassword == DataReader["Password"].ToString().Trim())
                        {
                            Correct = true;
                        }
                    }
                    DataReader.Close();
                }
                catch
                {
                    MessageBox.Show("错误！连接或查询数据库时出错");
                }
                finally
                {
                    conn.Close();
                }
            }
            DataPacket temp = new DataPacket();
            temp.Content = "";
            if (Correct)//旧密码输入正确，现在修改数据库中的密码为新密码
            {
                try
                {
                    conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                    cmd = new SqlCommand("update dbo.Administrator set Password = '" + newPassword + "' where AdministratorID='" + AdministratorID + "'", conn);
                    conn.Open();
                    cmd.ExecuteReader(CommandBehavior.CloseConnection);
                    temp.Content = "修改成功";
                }
                catch(Exception e)
                {
                    Console.WriteLine(e.ToString());
                    temp.Content = "数据库错误，修改失败";
                }
                finally
                {
                    conn.Close();
                }
            }
            else
            {
                temp.Content = "旧密码输入错误";
            }
            ToAdministratorModifyPassword(null, temp);
        }
        private void AcceptCallback(IAsyncResult iar)
        {
            //还原传入的原始套接字
            Socket server = iar.AsyncState as Socket;
            //在原始套接字上调用EndAccept方法，返回新套接字
            Socket service = server.EndAccept(iar);
            DataPacketManager recieveData = new DataPacketManager();
            recieveData.service = service;
            service.BeginReceive(recieveData.buffer, 0, DataPacketManager.MAX_SIZE, SocketFlags.None,
                new AsyncCallback(RecieveCallback), recieveData);
            server.BeginAccept(new AsyncCallback(AcceptCallback), server);//重新开始监听
        }
        private void RecieveCallback(IAsyncResult iar)
        {
            DataPacketManager recieveData = iar.AsyncState as DataPacketManager;
            int bytes = 0;
            try
            {
                bytes = recieveData.service.EndReceive(iar);
            }
            catch(Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.WriteLine("a client doesnt send anything");
            }
            if (bytes > 0)
            {
                DataPacket packet = new DataPacket(recieveData.buffer);
                //接下根据packet内的commandNo进行各种不同操作
                DataPacket responsePacket = new DataPacket();
                //根据接收到的数据包，产生响应的数据包
                responsePacket = Operate(packet, recieveData.service);
                //把响应的数据包发给客户端（不一定是原客户端）
                switch (responsePacket.CommandNo)
                {
                    case 1://客户端登录成功
                        {
                            //向客户端发送响应数据包
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            //在 在线用户表（在线的用户----对应的Socket）上面加上这条记录
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));//动态的反序列化，不删除Content后面的结束符的话无法反序列化
                            UserIDAndSocket line = new UserIDAndSocket();
                            line.UserID = content["UserID"];
                            line.Service = recieveData.service;
                            line.LastHeartBeatTime = DateTime.Now;
                            onlineUserList.Add(line);
                            Console.WriteLine("userIDAndSocketList added");
                            //向该用户的所有好友发送信息，提示该用户上线了
                            //...
                            try
                            {
                                SqlConnection selectConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                                SqlCommand selectCmd = new SqlCommand("select FriendID from dbo.Friends where ID='" + line.UserID + "'", selectConnect);
                                if (selectConnect.State == ConnectionState.Closed)
                                {
                                    try
                                    {
                                        selectConnect.Open();
                                        SqlDataReader DataReader = selectCmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                        while (DataReader.Read())//每找到一个该用户的好友
                                        {
                                            string friendID = DataReader["FriendID"].ToString();
                                            //在 在线用户表 里面寻找该用户的这个好友在不在线，若在线，就将该用户的上线信息发送给该好友
                                            for (int i = 0; i < onlineUserList.Count; i++)
                                            {
                                                if (((UserIDAndSocket)onlineUserList[i]).UserID == friendID)
                                                {
                                                    DataPacket tempPacket = new DataPacket();
                                                    tempPacket.CommandNo = 25;
                                                    tempPacket.Content = line.UserID;
                                                    tempPacket.ComputerName = "server";
                                                    tempPacket.NameLength = tempPacket.ComputerName.Length;
                                                    tempPacket.FromIP = IPAddress.Parse("0.0.0.0");
                                                    tempPacket.ToIP = IPAddress.Parse("0.0.0.0");
                                                    Send(((UserIDAndSocket)onlineUserList[i]).Service, tempPacket.PacketToBytes());
                                                    break;
                                                }
                                            }
                                        }
                                        DataReader.Close();
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e.ToString());
                                    }
                                    finally
                                    {
                                        selectConnect.Close();
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            //发布OneUserLogin事件
                            ToUpdateOnlineUserList(null, null);
                            break;
                        }
                    case 2://客户端登录失败
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 3://客户端注册成功
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 4://客户端注册失败
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 5://客户端修改密码成功
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 6://客户端修改密码失败
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 12://客户端添加好友失败
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 13://客户端删除好友成功
                        {
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                            string FriendID = content["FriendID"];
                            //将返回的数据包发送给被删好友的用户
                            for(int i=0; i<onlineUserList.Count; i++)
                            {
                                if(FriendID == ((UserIDAndSocket)onlineUserList[i]).UserID)
                                {
                                    Send(((UserIDAndSocket)onlineUserList[i]).Service, responsePacket.PacketToBytes());
                                }
                            }
                            break;
                        }
                    case 14://客户端删除好友失败，没有任何操作
                        {
                            break;
                        }
                    case 17://客户端下载好友列表成功
                        {
                            Console.WriteLine("a client want to download friendlist, success");
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 18://客户端下载好友列表失败
                        {
                            Console.WriteLine("a client want to download friendlist, fail");
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 19://客户端请求添加好友，该请求合法，则服务器转发该请求给被申请添加好友的用户
                        {
                            Console.WriteLine("a client want to add a friend, legal, sending...");
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                            string FriendID = content["FriendID"];
                            //确定这个包要通过哪个socket转发给用户（这个用户必须在线）
                            Socket targetSocket = null;
                            for(int i=0; i<onlineUserList.Count; i++)
                            {
                                if(FriendID == ((UserIDAndSocket)onlineUserList[i]).UserID)
                                {
                                    targetSocket = ((UserIDAndSocket)onlineUserList[i]).Service;
                                }
                            }
                            Send(targetSocket, responsePacket.PacketToBytes());
                            break;
                        }
                    case 20://收到添加好友申请用户同意了好友申请，现在向发起好友申请的客户端发送消息，使之更新好友列表
                        {
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            dynamic content = js.Deserialize<dynamic>(responsePacket.Content.Replace("\0", ""));
                            for (int i = 0; i < onlineUserList.Count; i++)
                            {
                                if (((UserIDAndSocket)onlineUserList[i]).UserID == content["UserID"])
                                {
                                    Send(((UserIDAndSocket)onlineUserList[i]).Service, responsePacket.PacketToBytes());
                                }
                            }
                            break;
                        }
                    case 21://客户端请求发送即时消息合法，予以转发
                        {
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            dynamic content = js.Deserialize<dynamic>(responsePacket.Content.Replace("\0", ""));
                            string targetUserID = content["TargetUserID"];
                            bool success = false;
                            for(int i=0; i<onlineUserList.Count; i++)
                            {
                                if(targetUserID == ((UserIDAndSocket)onlineUserList[i]).UserID)
                                {
                                    Send(((UserIDAndSocket)onlineUserList[i]).Service, responsePacket.PacketToBytes());
                                    success = true;
                                }
                            }
                            break;
                        }
                    case 26://客户端请求发送即时消息非法，打回到原客户端
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    case 255:
                        {
                            Send(recieveData.service, responsePacket.PacketToBytes());
                            break;
                        }
                    default:
                        break;
                }
                DataPacketManager newRecieveData = new DataPacketManager();
                newRecieveData.service = recieveData.service;
                newRecieveData.service.BeginReceive(newRecieveData.buffer, 0, DataPacketManager.MAX_SIZE, SocketFlags.None,
                    new AsyncCallback(RecieveCallback), newRecieveData);
            }
            else
            {
                try
                {
                    recieveData.service.BeginReceive(recieveData.buffer, 0, DataPacketManager.MAX_SIZE, SocketFlags.None,
                new AsyncCallback(RecieveCallback), recieveData);
                }
                catch(Exception e)
                {
                    Console.WriteLine(e.ToString());
                    Console.WriteLine("a client may has been closed");
                }
            }
        }
        public DataPacket Operate(DataPacket packet, Socket service)
        {
            DataPacket responsePacket = new DataPacket();
            responsePacket.CommandNo = 255;//表示数据包里无有效信息
            responsePacket.ComputerName = "server";
            responsePacket.NameLength = responsePacket.ComputerName.Length;
            responsePacket.FromIP = IPAddress.Parse("0.0.0.0");
            responsePacket.ToIP = IPAddress.Parse("0.0.0.0");
            responsePacket.Content = "";
            switch (packet.CommandNo)
            {
                case 1://客户端请求登录操作
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        string input_ID = "null";
                        string input_PW = "null";
                        try
                        {
                            dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));//动态的反序列化，不删除Content后面的结束符的话无法反序列化
                            input_ID = content["UserID"];//动态反序列化的结果必须用索引取值
                            input_PW = content["Password"];
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }

                        bool Correct = false;
                        SqlConnection conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand cmd = new SqlCommand("select Password from dbo.Users where UserID='" + input_ID + "'", conn);
                        if (conn.State == ConnectionState.Closed)
                        {
                            try
                            {
                                conn.Open();
                                SqlDataReader DataReader = cmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                while (DataReader.Read())
                                {
                                    if (input_PW == DataReader["Password"].ToString().Trim())
                                    {
                                        Correct = true;
                                    }
                                }
                                DataReader.Close();
                            }
                            catch(Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                conn.Close();
                            }
                        }
                        //构造要向客户端发送的数据包
                        if (Correct == true)
                        {
                            responsePacket.CommandNo = 1;
                            //在数据库的Stat表中插入一条记录
                            try
                            {
                                SqlConnection insertConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                                SqlCommand insertCmd = new SqlCommand("insert into dbo.Stat values('" + DateTime.Now + "', '" + input_ID + "', '" + "Login" + "', '" + " " + "')", insertConnect);
                                if (insertConnect.State == ConnectionState.Closed)
                                {
                                    try
                                    {
                                        insertConnect.Open();
                                        insertCmd.ExecuteNonQuery();
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e.ToString());
                                    }
                                    finally
                                    {
                                        insertConnect.Close();
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                        }
                        else
                        {
                            responsePacket.CommandNo = 2;
                        }
                        responsePacket.Content = input_ID;
                        break;
                    }
                case 2://客户端请求注册操作
                    {
                        //先从数据库找出所有已经已存在的UserID，构造一个尚未存在的UserID
                        int UserID;
                        ArrayList ExistID = new ArrayList(10);

                        SqlConnection selectConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand selectCmd = new SqlCommand("select UserID from dbo.Users", selectConnect);
                        if (selectConnect.State == ConnectionState.Closed)
                        {
                            try
                            {
                                selectConnect.Open();
                                SqlDataReader DataReader = selectCmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                while (DataReader.Read())
                                {
                                    //DataReader["UserID"]返回的数据的类型和数据库存储的类型一致，此处为int32
                                    ExistID.Add(DataReader["UserID"]);
                                }
                                DataReader.Close();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                selectConnect.Close();
                            }
                        }
                        ExistID.Sort();//所有ID都从小到大排序了
                        UserID = (int)ExistID[ExistID.Count - 1] + 1;

                        //将构造出的新ID和packet里面的密码存储到数据库
                        string PW = "";
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        try
                        {
                            dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));//动态的反序列化，不删除Content后面的结束符的话无法反序列化
                            PW = content["Password"];//动态反序列化的结果必须用索引取值
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                        SqlConnection insertConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand insertCmd = new SqlCommand("insert into dbo.Users values('" + UserID.ToString() + "', null, '" + PW + "', null, null, null, null, null, null, null, null, null)", insertConnect);
                        bool registerSuccess = true;
                        if (insertConnect.State == ConnectionState.Closed)/*有问题，但是没出错*/
                        {
                            try
                            {
                                insertConnect.Open();
                                insertCmd.ExecuteNonQuery();//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                                registerSuccess = false;
                            }
                            finally
                            {
                                insertConnect.Close();
                            }
                        }
                        //构造要发送的数据包
                        if (registerSuccess == true)
                        {
                            responsePacket.CommandNo = 3;
                        }
                        else
                        {
                            responsePacket.CommandNo = 4;
                        }
                        responsePacket.Content = UserID.ToString();
                        break;
                    }
                case 3://客户端请求修改密码操作
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                        string UserID = content["UserID"];
                        string OldPassword = content["OldPassword"];
                        string NewPassword = content["NewPassword"];
                        //查询原密码是否正确
                        bool correct = false;
                        SqlConnection conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand cmd = new SqlCommand("select Password from dbo.Users where UserID='" + UserID + "'", conn);
                        if (conn.State == ConnectionState.Closed)
                        {
                            try
                            {
                                conn.Open();
                                SqlDataReader DataReader = cmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                while (DataReader.Read())
                                {
                                    if (OldPassword == DataReader["Password"].ToString().Trim())
                                    {
                                        correct = true;
                                    }
                                }
                                DataReader.Close();
                            }
                            catch
                            {
                                MessageBox.Show("错误！连接或查询数据库时出错");
                            }
                            finally
                            {
                                conn.Close();
                            }
                        }
                        //若原密码正确，修改数据库
                        if (correct)
                        {
                            try
                            {
                                conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                                cmd = new SqlCommand("update dbo.Users set Password = '" + NewPassword + "' where UserID='" + UserID + "'", conn);
                                conn.Open();
                                cmd.ExecuteReader(CommandBehavior.CloseConnection);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                conn.Close();
                            }
                        }
                        //构造返回的数据包
                        if (correct)
                        {
                            responsePacket.CommandNo = 5;
                            //在数据库的Stat表中插入一条记录
                            try
                            {
                                SqlConnection insertConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                                SqlCommand insertCmd = new SqlCommand("insert into dbo.Stat values('" + DateTime.Now + "', '" + UserID + "', '" + "ChangePassword" + "', '" + " " + "')", insertConnect);
                                if (insertConnect.State == ConnectionState.Closed)
                                {
                                    try
                                    {
                                        insertConnect.Open();
                                        insertCmd.ExecuteNonQuery();
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e.ToString());
                                    }
                                    finally
                                    {
                                        insertConnect.Close();
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                        }
                        else
                        {
                            responsePacket.CommandNo = 6;
                        }
                        break;
                    }
                case 6://客户端请求添加好友操作
                    {
                        Console.WriteLine("operate case 6");
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                        string FriendID = content["FriendID"];
                        string UserID = content["UserID"];
                        //在数据库中搜索FriendID，判断UserID是否已经加FriendID为好友，若是，则添加好友失败，只构造一个返回给UserID的包。
                        bool isFriendAlready = false;
                        SqlConnection conn = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand cmd = new SqlCommand("select FriendID from dbo.Friends where ID='" + UserID + "'", conn);
                        if (conn.State == ConnectionState.Closed)
                        {
                            try
                            {
                                conn.Open();
                                SqlDataReader DataReader = cmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                while (DataReader.Read())
                                {
                                    if (FriendID == DataReader["FriendID"].ToString().Trim())
                                    {
                                        isFriendAlready = true;
                                    }
                                }
                                DataReader.Close();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                conn.Close();
                            }
                        }
                        if (isFriendAlready)
                        {
                            responsePacket.CommandNo = 12;
                            responsePacket.Content = "错误：你和该用户已经是好友了";
                            break;
                        }
                        //即使UserID和FriendID还不是好友，如果User表中不存在FriendID，则添加好友也失败，只构造一个返回给UserID的包。
                        bool friendIDExist = false;
                        SqlConnection friendIDExistConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand friendIDExistCmd = new SqlCommand("select UserID from dbo.Users where UserID='" + FriendID + "'", friendIDExistConnect);
                        if (friendIDExistConnect.State == ConnectionState.Closed)
                        {
                            try
                            {
                                friendIDExistConnect.Open();
                                SqlDataReader DataReader = friendIDExistCmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                while (DataReader.Read())
                                {
                                    if (FriendID == DataReader["UserID"].ToString().Trim())
                                    {
                                        friendIDExist = true;
                                    }
                                }
                                DataReader.Close();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                friendIDExistConnect.Close();
                            }
                        }
                        if (!friendIDExist)
                        {
                            responsePacket.CommandNo = 12;
                            responsePacket.Content = "错误：不存在这样的用户";
                            break;
                        }
                        //即使允许加FriendID为好友，若FriendID不在线，则加好友失败。只构造一个返回给UserID的包。
                        bool friendIDIsOnline = false;
                        for (int i=0; i<onlineUserList.Count; i++)
                        {
                            if (((UserIDAndSocket)onlineUserList[i]).UserID == FriendID) { friendIDIsOnline = true; }
                        }
                        if(friendIDIsOnline == false)
                        {
                            responsePacket.CommandNo = 12;
                            responsePacket.Content = "错误：该用户现在不在线";
                            Console.WriteLine("FriendID不在线");
                            break;
                        }
                        //若FriendID在线，而且UserID可以加FriendID为好友，则构造一个发给FriendID的数据包，内容是UserID的请求信息。
                        responsePacket.CommandNo = 19;
                        responsePacket.Content = packet.Content;
                        break;
                    }
                case 7://客户端请求删除好友操作
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                        string UserID = content["UserID"];
                        string FriendID = content["FriendID"];
                        //构造要发送被删好友的用户的数据包
                        responsePacket.Content = packet.Content;
                        responsePacket.CommandNo = 14;
                        //在数据库中删除好友关系
                        SqlConnection connect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand cmd = new SqlCommand("delete from dbo.Friends where ID like '" + UserID + "' and FriendID like '" + FriendID + "'", connect);
                        SqlCommand cmd2 = new SqlCommand("delete from dbo.Friends where ID like '" + FriendID + "' and FriendID like '" + UserID + "'", connect);
                        if (connect.State == ConnectionState.Closed)
                        {
                            try
                            {
                                connect.Open();
                                cmd.ExecuteNonQuery();//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                cmd2.ExecuteNonQuery();
                                responsePacket.CommandNo = 13;
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                connect.Close();
                            }
                        }
                        //在数据库的Stat表中插入一条记录
                        try
                        {
                            SqlConnection insertConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                            SqlCommand insertCmd = new SqlCommand("insert into dbo.Stat values('" + DateTime.Now + "', '" + UserID + "', '" + "DeleteFriend" + "', '" + FriendID + "')", insertConnect);
                            if (insertConnect.State == ConnectionState.Closed)
                            {
                                try
                                {
                                    insertConnect.Open();
                                    insertCmd.ExecuteNonQuery();
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.ToString());
                                }
                                finally
                                {
                                    insertConnect.Close();
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                        break;
                    }
                case 9://客户端请求下载好友列表
                    {
                        Console.WriteLine("operate case 9");
                        //构造返回的数据包
                        responsePacket.ComputerName = "server";
                        responsePacket.NameLength = responsePacket.ComputerName.Length;
                        responsePacket.FromIP = IPAddress.Parse("0.0.0.0");
                        responsePacket.ToIP = IPAddress.Parse("0.0.0.0");
                        //在数据库的Friends表中搜索，得到这个用户的所有好友的ID
                        string UserID = packet.Content.Replace("\0", "");
                        ArrayList friendList = new ArrayList();
                        bool success = false;
                        try
                        {
                            SqlConnection selectConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                            SqlCommand selectCmd = new SqlCommand("select FriendID from dbo.Friends where ID='" + UserID + "'", selectConnect);
                            if (selectConnect.State == ConnectionState.Closed)
                            {
                                try
                                {
                                    selectConnect.Open();
                                    SqlDataReader DataReader = selectCmd.ExecuteReader(CommandBehavior.CloseConnection);//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                    while (DataReader.Read())
                                    {
                                        FriendListItem item = new FriendListItem();
                                        item.UserID = DataReader["FriendID"].ToString();
                                        item.IsOnline = false;
                                        for(int i=0; i<onlineUserList.Count; i++)
                                        {
                                            if(((UserIDAndSocket)onlineUserList[i]).UserID == item.UserID)
                                            {
                                                item.IsOnline = true;
                                                break;
                                            }
                                        }
                                        friendList.Add(item);
                                    }
                                    DataReader.Close();
                                }
                                catch(Exception e)
                                {
                                    Console.WriteLine(e.ToString());
                                }
                                finally
                                {
                                    selectConnect.Close();
                                }
                            }
                            success = true;
                        }
                        catch(Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                        //处理返回的数据包的Content和CommandNO部分
                        if(success == true)
                        {
                            responsePacket.CommandNo = 17;
                        }
                        else
                        {
                            responsePacket.CommandNo = 18;
                        }
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        responsePacket.Content = js.Serialize(friendList);
                        break;
                    }
                case 10://收到添加好友申请用户同意了好友申请
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                        string UserID = content["UserID"];
                        string FriendID = content["FriendID"];
                        //添加好友关系到数据库（有两行：a和b是好友、b和a是好友）
                        SqlConnection insertConnect = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                        SqlCommand insertCmd = new SqlCommand("insert into dbo.Friends values('" + UserID + "', '" + FriendID + "')", insertConnect);
                        SqlCommand reverseInsertCmd = new SqlCommand("insert into dbo.Friends values('" + FriendID + "', '" + UserID + "')", insertConnect);
                        if (insertConnect.State == ConnectionState.Closed)
                        {
                            try
                            {
                                insertConnect.Open();
                                insertCmd.ExecuteNonQuery();//使用这种方式构造SqlDataReader类型的对象，能够保证在DataReader关闭后自动Close()对应的SqlConnection类型的对象
                                reverseInsertCmd.ExecuteNonQuery();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.ToString());
                            }
                            finally
                            {
                                insertConnect.Close();
                            }
                        }
                        //发包提示好友申请发起者，别人已经同意添加好友
                        //...
                        responsePacket.CommandNo = 20;
                        responsePacket.Content = packet.Content;
                        //在数据库的Stat表中插入一条记录
                        try
                        {
                            SqlConnection insertConnect_2 = new SqlConnection("Data Source=" + DataSourceName + ";Initial Catalog=JinNangIM_DB;Integrated Security=True");
                            SqlCommand insertCmd_2 = new SqlCommand("insert into dbo.Stat values('" + DateTime.Now + "', '" + UserID + "', '" + "ConfirmFriendRequest" + "', '" + FriendID + "')", insertConnect_2);
                            if (insertConnect_2.State == ConnectionState.Closed)
                            {
                                try
                                {
                                    insertConnect_2.Open();
                                    insertCmd_2.ExecuteNonQuery();
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.ToString());
                                }
                                finally
                                {
                                    insertConnect_2.Close();
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                        break;
                    }
                case 11://客户端请求发送即时消息
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        dynamic content = js.Deserialize<dynamic>(packet.Content.Replace("\0", ""));
                        string targetUserID = content["TargetUserID"];
                        //判断目标用户是否在线
                        bool isOnline = false;
                        for(int i=0; i<onlineUserList.Count; i++)
                        {
                            if (((UserIDAndSocket)onlineUserList[i]).UserID == targetUserID)
                            {
                                isOnline = true;
                                break;
                            }
                        }
                        if (isOnline)
                        {
                            responsePacket.CommandNo = 21;
                            responsePacket.Content = packet.Content;
                        }
                        else
                        {
                            responsePacket.CommandNo = 26;
                            responsePacket.Content = "发送失败";
                        }
                        break;
                    }
                case 254://客户端发送的心跳包
                    {
                        responsePacket.CommandNo = 254;
                        responsePacket.Content = "";
                        Console.WriteLine("heart beat");
                        string sourceUserID = packet.Content.Replace("\0", "");
                        //在 在线用户表 里面查找这个用户，更新最后一次收到心跳包的时间
                        for(int i=0; i<onlineUserList.Count; i++)
                        {
                            if(((UserIDAndSocket)onlineUserList[i]).UserID == sourceUserID)
                            {
                                ((UserIDAndSocket)onlineUserList[i]).LastHeartBeatTime = DateTime.Now;
                            }
                        }
                        break;
                    }
                case 255://客户端启动，请求连接服务端
                    {
                        responsePacket.ComputerName = "server";
                        responsePacket.NameLength = responsePacket.ComputerName.Length;
                        responsePacket.FromIP = IPAddress.Parse("0.0.0.0");
                        responsePacket.ToIP = IPAddress.Parse("0.0.0.0");
                        responsePacket.CommandNo = 255;
                        responsePacket.Content = "";
                        break;
                    }
                default:
                    break;
            }
            return responsePacket;
        }
        private void Send(Socket handler, byte[] buffer)
        {
            handler.BeginSend(buffer, 0, buffer.Length, 0, new AsyncCallback(SendCallback), handler);
        }
        private void SendCallback(IAsyncResult iar)
        {
            try
            {
                //重新获取socket
                Socket handler = (Socket)iar.AsyncState;
                /*service.BeginReceive(recieveData.buffer, 0, DataPacketManager.MAX_SIZE, SocketFlags.None,
                new AsyncCallback(RecieveCallback), recieveData);*/
                //完成发送字节数组动作
                int bytesSent = handler.EndSend(iar);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        /// <summary>
        /// getter setter
        /// </summary>
        public bool ServerIsRunning
        {
            get{ return serverIsRunning; }
        }
    }
}
