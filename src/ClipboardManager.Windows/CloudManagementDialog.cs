using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using TabControl = System.Windows.Controls.TabControl;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using System.IO;

namespace ClipboardManager.Windows;

public sealed class CloudManagementDialog : Window
{
    private readonly ICloudWorkspaceService _service;
    private readonly ComboBox _workspaces = new() { MinWidth = 220, DisplayMemberPath = nameof(WorkspaceRow.Label) };
    private readonly ListBox _members = new();
    private readonly ListBox _groups = new();
    private readonly ListBox _devices = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    public CloudManagementDialog(ICloudWorkspaceService service)
    {
        _service = service;
        Title="云端管理"; Width=720; Height=520; MinWidth=620; MinHeight=440;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;
        var root=new Grid{Margin=new Thickness(18)}; root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var bar=new StackPanel{Orientation=Orientation.Horizontal};
        bar.Children.Add(_workspaces); _workspaces.SelectionChanged+=async(_,_)=>await SafeAsync(RefreshDetailsAsync);
        bar.Children.Add(Button("新建团队",async()=>{var name=Prompt("新建团队","团队名称");if(!string.IsNullOrWhiteSpace(name)){await _service.CreateTeamAsync(name);await LoadAsync();}}));
        bar.Children.Add(Button("加入团队",async()=>{var code=Prompt("加入团队","邀请码");if(!string.IsNullOrWhiteSpace(code)){await _service.JoinAsync(code);await LoadAsync();}}));
        root.Children.Add(bar);
        var tabs=new TabControl{Margin=new Thickness(0,12,0,0)};Grid.SetRow(tabs,1);
        tabs.Items.Add(Tab("成员",_members, Button("生成邀请码",CreateInvitationAsync), Button("修改角色",ChangeRoleAsync), Button("移除成员",RemoveMemberAsync)));
        tabs.Items.Add(Tab("分组",_groups, Button("新建分组",CreateGroupAsync), Button("配置成员",ConfigureGroupAsync)));
        tabs.Items.Add(Tab("设备",_devices, Button("刷新设备",RefreshDevicesAsync), Button("撤销设备",RevokeDeviceAsync)));
        root.Children.Add(tabs);Grid.SetRow(_status,2);root.Children.Add(_status);Content=root;
        Loaded+=async(_,_)=>await SafeAsync(LoadAsync);
    }

    private Button Button(string text,Func<Task> action)
    {
        var button=new Button{Content=text,Margin=new Thickness(8,0,0,0),Padding=new Thickness(12,6,12,6)};
        button.Click+=async(_,_)=>await SafeAsync(action);return button;
    }
    private static TabItem Tab(string header,ListBox list,params Button[] actions)
    {
        var grid=new Grid{Margin=new Thickness(8)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        var bar=new WrapPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Right};foreach(var action in actions)bar.Children.Add(action);grid.Children.Add(bar);Grid.SetRow(list,1);list.Margin=new Thickness(0,8,0,0);grid.Children.Add(list);return new TabItem{Header=header,Content=grid};
    }
    private async Task LoadAsync()
    {
        var selected=Selected?.Workspace.Id;var rows=(await _service.GetAvailableAsync()).Where(x=>x.Type==WorkspaceType.Team).Select(x=>new WorkspaceRow(x,$"{x.Name} · {x.Role}")).ToArray();
        _workspaces.ItemsSource=rows;_workspaces.SelectedItem=rows.FirstOrDefault(x=>x.Workspace.Id==selected)??rows.FirstOrDefault();
        await RefreshDevicesAsync();_status.Text=rows.Length==0?"尚未创建或加入团队。":"云端修改会写入审计日志；Member 仅可读取团队配置。";
    }
    private WorkspaceRow? Selected=>_workspaces.SelectedItem as WorkspaceRow;
    private async Task RefreshDetailsAsync()
    {
        if(Selected is null){_members.ItemsSource=null;_groups.ItemsSource=null;return;}
        var w=Selected.Workspace.Id;var members=await _service.GetMembersAsync(w);var groups=await _service.GetGroupsAsync(w);
        if(Selected?.Workspace.Id!=w)return;
        _members.DisplayMemberPath="Label";_members.ItemsSource=members.Select(x=>new MemberRow(x,$"{x.DisplayName}  {x.Email}  · {x.Role} · {x.GroupIds.Count} 个分组")).ToArray();
        _groups.DisplayMemberPath="Label";_groups.ItemsSource=groups.Select(x=>new GroupRow(x,$"{x.Name}  · {x.MemberCount} 人")).ToArray();
    }
    private async Task CreateInvitationAsync()
    {
        if(Selected is null)throw new InvalidOperationException("请先选择团队。");
        var invite=await _service.CreateInvitationAsync(Selected.Workspace.Id,TimeSpan.FromHours(24),1);
        if(string.IsNullOrWhiteSpace(invite.Code))throw new InvalidDataException("服务端未返回邀请码。");
        MessageBox.Show(this,$"邀请码：{invite.Code}\n有效期至 {invite.ExpiresAt.ToLocalTime():g}，限用 1 次。\n请妥善传递给受邀人员。","邀请码",MessageBoxButton.OK,MessageBoxImage.Information);
        _status.Text="邀请码仅展示一次，不会自动写入剪切板历史。";
    }
    private async Task CreateGroupAsync()
    {
        if(Selected is null)throw new InvalidOperationException("请先选择团队。");
        var name=Prompt("新建分组","分组名称");if(string.IsNullOrWhiteSpace(name))return;
        await _service.CreateGroupAsync(Selected.Workspace.Id,name);await RefreshDetailsAsync();
    }
    private async Task RefreshDevicesAsync(){_devices.DisplayMemberPath="Label";_devices.ItemsSource=(await _service.GetDevicesAsync()).Select(x=>new DeviceRow(x,$"{x.Name} · {(x.IsCurrent?"当前设备":x.RevokedAt is null?"有效":"已撤销")} · {x.LastSeenAt?.ToLocalTime():g}")).ToArray();}
    private async Task ChangeRoleAsync()
    {
        if(Selected?.Workspace.Role!=WorkspaceRole.Owner)throw new InvalidOperationException("只有主账号可以修改角色。");
        if(_members.SelectedItem is not MemberRow row)throw new InvalidOperationException("请选择成员。");
        if(row.Member.Role==WorkspaceRole.Owner)throw new InvalidOperationException("不能在此修改主账号。");
        var role=row.Member.Role==WorkspaceRole.Admin?WorkspaceRole.Member:WorkspaceRole.Admin;
        if(!Confirm($"将 {row.Member.Email} 的角色改为 {role}？"))return;
        await _service.UpdateMemberRoleAsync(Selected.Workspace.Id,row.Member.UserId,role);await RefreshDetailsAsync();
    }
    private async Task RemoveMemberAsync()
    {
        if(Selected is null||_members.SelectedItem is not MemberRow row)throw new InvalidOperationException("请选择团队成员。");
        if(!Confirm($"从团队移除 {row.Member.Email}？该成员将失去云端访问权限。"))return;
        await _service.RemoveMemberAsync(Selected.Workspace.Id,row.Member.UserId);await RefreshDetailsAsync();
    }
    private async Task RevokeDeviceAsync()
    {
        if(_devices.SelectedItem is not DeviceRow row)throw new InvalidOperationException("请选择设备。");
        if(row.Device.IsCurrent)throw new InvalidOperationException("当前设备请使用退出账号。");
        if(!Confirm($"撤销设备 {row.Device.Name} 的登录会话？"))return;
        await _service.RevokeDeviceAsync(row.Device.Id);await RefreshDevicesAsync();
    }
    private async Task ConfigureGroupAsync()
    {
        if(Selected is null||_groups.SelectedItem is not GroupRow row)throw new InvalidOperationException("请选择分组。");
        var workspace=Selected.Workspace.Id;var members=await _service.GetMembersAsync(workspace);
        var panel=new StackPanel{Margin=new Thickness(16)};var boxes=members.Select(m=>(Member:m,Box:new System.Windows.Controls.CheckBox{Content=$"{m.DisplayName} · {m.Email}",IsChecked=m.GroupIds.Contains(row.Group.Id),Margin=new Thickness(0,6,0,6)})).ToArray();
        foreach(var entry in boxes)panel.Children.Add(entry.Box);
        var dialog=new Window{Owner=this,Title=$"配置分组：{row.Group.Name}",Width=460,Height=420,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        if(TryFindResource("DialogWindowStyle") is Style style)dialog.Style=style;
        var save=new Button{Content="保存",Margin=new Thickness(0,12,0,0)};save.Click+=(_,_)=>dialog.DialogResult=true;panel.Children.Add(save);dialog.Content=new ScrollViewer{Content=panel};
        if(dialog.ShowDialog()!=true)return;
        await _service.SetGroupMembersAsync(workspace,row.Group.Id,boxes.Where(x=>x.Box.IsChecked==true).Select(x=>x.Member.UserId).ToArray());await RefreshDetailsAsync();
    }
    private bool Confirm(string text)=>MessageBox.Show(this,text,"确认云端操作",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    private int _busy;
    private async Task SafeAsync(Func<Task> action){try{_busy++;await action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"云端管理失败",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{_busy--;}}
    private string? Prompt(string title,string label){var dialog=new SingleInputDialog(this,title,label);return dialog.ShowDialog()==true?dialog.Value:null;}
    private sealed record WorkspaceRow(CloudWorkspace Workspace,string Label);
    private sealed record MemberRow(TeamMember Member,string Label);
    private sealed record GroupRow(MemberGroup Group,string Label);
    private sealed record DeviceRow(CloudDevice Device,string Label);
}
