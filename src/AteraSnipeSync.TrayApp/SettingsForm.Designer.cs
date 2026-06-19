namespace AteraSnipeSync.TrayApp;

partial class SettingsForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        titleLabel = new Label();
        ateraApiKeyLabel = new Label();
        ateraApiKeyTextBox = new TextBox();
        saveButton = new Button();
        statusLabel = new Label();
        SuspendLayout();
        // 
        // titleLabel
        // 
        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
        titleLabel.Location = new Point(24, 22);
        titleLabel.Name = "titleLabel";
        titleLabel.Size = new Size(223, 25);
        titleLabel.TabIndex = 0;
        titleLabel.Text = "Atera Snipe-IT Sync";
        // 
        // ateraApiKeyLabel
        // 
        ateraApiKeyLabel.AutoSize = true;
        ateraApiKeyLabel.Location = new Point(27, 76);
        ateraApiKeyLabel.Name = "ateraApiKeyLabel";
        ateraApiKeyLabel.Size = new Size(82, 15);
        ateraApiKeyLabel.TabIndex = 1;
        ateraApiKeyLabel.Text = "Atera API Key";
        // 
        // ateraApiKeyTextBox
        // 
        ateraApiKeyTextBox.Location = new Point(27, 99);
        ateraApiKeyTextBox.Name = "ateraApiKeyTextBox";
        ateraApiKeyTextBox.Size = new Size(420, 23);
        ateraApiKeyTextBox.TabIndex = 2;
        ateraApiKeyTextBox.UseSystemPasswordChar = true;
        // 
        // saveButton
        // 
        saveButton.Location = new Point(27, 145);
        saveButton.Name = "saveButton";
        saveButton.Size = new Size(96, 30);
        saveButton.TabIndex = 3;
        saveButton.Text = "Save";
        saveButton.UseVisualStyleBackColor = true;
        saveButton.Click += SaveButton_Click;
        // 
        // statusLabel
        // 
        statusLabel.AutoSize = true;
        statusLabel.Location = new Point(142, 153);
        statusLabel.MaximumSize = new Size(305, 0);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(0, 15);
        statusLabel.TabIndex = 4;
        // 
        // SettingsForm
        // 
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(484, 211);
        Controls.Add(statusLabel);
        Controls.Add(saveButton);
        Controls.Add(ateraApiKeyTextBox);
        Controls.Add(ateraApiKeyLabel);
        Controls.Add(titleLabel);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Name = "SettingsForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Atera Snipe-IT Sync";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Label titleLabel;
    private Label ateraApiKeyLabel;
    private TextBox ateraApiKeyTextBox;
    private Button saveButton;
    private Label statusLabel;
}
